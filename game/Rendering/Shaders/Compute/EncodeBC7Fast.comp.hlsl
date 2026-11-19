// Derived from basis_universal (https://github.com/BinomialLLC/basis_universal), Copyright (C) 2016-2026 Binomial LLC.
// Licensed under the Apache License, Version 2.0. Modified from the original.

// A port of Bc7BlockEncoder (basis_universal's bc7f). bc7f predicts one configuration per block instead of
// searching, so a thread group has nothing to cooperate on.
//
// Not byte-identical to Bc7BlockEncoder: it runs GpuFlags, rounds selectors from a float scale instead of
// searching, and fma contraction moves the estimates that pick a mode. It is compared by PSNR and mode
// histogram, and the page cache stamp carries the encoder identity.
//
// The PackFlag* bits are the compile-time GpuFlags, not pc.flags, so the compiler drops the unused modes and
// their registers, which on Apple GPUs is most of the cost.
//
// Loop counters are numbered because slang's HLSL mode hoists a for-loop declaration to function scope.
// `line` and `shared` are avoided, and no ternary returns a struct, so the source also compiles with dxc.

#include "BCEncodeCommon.hlsl"
#include "BCn/bc7f_tables.h"

#define FLAG_USE_2_SUBSETS_RGB   1u
#define FLAG_USE_2_SUBSETS_RGBA  2u
#define FLAG_USE_3_SUBSETS_RGB   4u
#define FLAG_USE_DUAL_PLANE_RGB  8u
#define FLAG_USE_DUAL_PLANE_RGBA 16u
#define FLAG_PBIT_OPT            32u
#define FLAG_PBIT_OPT_MODE6      64u
#define FLAG_USE_TRIVIAL_MODE6   128u

static const float SmallFloatValue = .0000125f;
static const float ProjectionEpsilon = .00000125f;
static const float UniquePBitDiscount = .85f;
static const float SharedPBitDiscount = .95f;
static const float NoCandidate = 1e+9f;

static const int TrivialBlockThreshRgb = 20 * 16;
static const int TrivialBlockThreshRgba = 2 * 16;
static const int DpBlockVarThresh = 2 * 16;
static const float StrongCorrThresh = .85f;
static const int MinBlockMaxVar23Subsets = 100 * 16;
static const float HighOrthoEnergyThresh = 1.0f * 16.0f;
static const float OrthoRatio23SubsetRatioThresh = .004f;
static const int DpBlockVarThreshRgba = 1 * 16;
static const float AlphaDecorrThreshold = .995f;
static const float StrongDecorrThreshRgba = .85f;
static const int MinBlockMaxVar3Subsets = 500 * 16;
static const int MinBlockMaxVar23SubsetsRgba = 100 * 16;
static const float HighOrthoEnergyThreshRgba = 1.0f * 16.0f;
static const float OrthoRatio23SubsetRatioThreshRgba = .004f;

// C#'s Truncate is a plain cast, not the saturating conversion; every input is in range.
int truncate(float x) { return (int)x; }
int roundPositive(float x) { return (int)(x + .5f); }
int roundHalfAway(float x) { return (int)(x >= 0.f ? x + .5f : x - .5f); }

int from4(int v, int p) { v = (v << 1) | p; return (v << 3) | (v >> 2); }
int from5(int v) { return (v << 3) | (v >> 2); }
int from5p(int v, int p) { v = (v << 1) | p; return (v << 2) | (v >> 4); }
int from6(int v, int p) { v = (v << 1) | p; return (v << 1) | (v >> 6); }
int from6(int v) { return (v << 2) | (v >> 4); }
int from7(int v) { return (v << 1) | (v >> 6); }
int from7p(int v, int p) { return (v << 1) | p; }

int to5(int c8) { return (c8 * 31 + 127) / 255; }
int to6(int c8) { return (c8 * 63 + 127) / 255; }
int to7(int c8) { return (c8 * 127 + 127) / 255; }
int to7p(int c8, int pbit) { return min(127, (int)(((uint)c8 + (uint)(pbit ^ 1)) >> 1)); }
int to7pf(float c, int pbit) { return to7p(roundPositive(c), pbit); }
int to7pClamp(float c, int pbit) { return to7p(clamp(roundHalfAway(c), 0, 255), pbit); }

int to6p(int c8, int pbit)
{
    int q7 = (c8 * 127 + 127) / 255;
    if ((q7 & 1) != pbit)
        q7 = c8 * 127 >= 255 * q7 ? (q7 < 127 ? q7 + 1 : q7 - 1) : (q7 > 0 ? q7 - 1 : q7 + 1);
    return q7 >> 1;
}

int to5p(int c8, int pbit)
{
    int q6 = (c8 * 63 + 127) / 255;
    if ((q6 & 1) != pbit)
        q6 = c8 * 63 >= 255 * q6 ? (q6 < 63 ? q6 + 1 : q6 - 1) : (q6 > 0 ? q6 - 1 : q6 + 1);
    return q6 >> 1;
}

int to6pClamp(float c, int pbit) { return to6p(clamp(roundHalfAway(c), 0, 255), pbit); }
int to5pClamp(float c, int pbit) { return to5p(clamp(roundHalfAway(c), 0, 255), pbit); }
int to5Clamp(float c) { return clamp(roundHalfAway(c * (31.f / 255.f)), 0, 31); }

// The BC7 weight tables: round(i * 64 / mask), exact for mask 3, 7 and 15.
uint weightFor(uint i, uint mask) { return (i * 128u + mask) / (2u * mask); }

bool invert2x2(float z00, float z10, float z11, out float4 iz)
{
    float det = z00 * z11 - z10 * z10;
    if (abs(det) < 1e-8f)
    {
        iz = float4(0, 0, 0, 0);
        return false;
    }
    det = 1.f / det;
    iz = float4(z11, -z10, -z10, z00) * det;
    return true;
}

int4 unpack(uint texel) { return int4(texel, texel >> 8, texel >> 16, texel >> 24) & 255; }

// Spelled out per texel, with channel 3 swapping alpha with itself instead of copying: as a loop, [unroll] or
// not, it reached Metal still rolled, the rotated uint[16] went to stack memory, and the M1 kernel was 6% slower.
uint swapIntoAlpha(uint texel, uint shift)
{
    uint moved = (texel >> shift) & 0xFFu;
    uint alpha = texel >> 24u;
    return (texel & ~((0xFFu << shift) | 0xFF000000u)) | (alpha << shift) | (moved << 24u);
}

void rotateChannelIntoAlpha(uint px[16], uint channel, out uint out_[16])
{
    uint shift = channel * 8u;
    out_[0] = swapIntoAlpha(px[0], shift);
    out_[1] = swapIntoAlpha(px[1], shift);
    out_[2] = swapIntoAlpha(px[2], shift);
    out_[3] = swapIntoAlpha(px[3], shift);
    out_[4] = swapIntoAlpha(px[4], shift);
    out_[5] = swapIntoAlpha(px[5], shift);
    out_[6] = swapIntoAlpha(px[6], shift);
    out_[7] = swapIntoAlpha(px[7], shift);
    out_[8] = swapIntoAlpha(px[8], shift);
    out_[9] = swapIntoAlpha(px[9], shift);
    out_[10] = swapIntoAlpha(px[10], shift);
    out_[11] = swapIntoAlpha(px[11], shift);
    out_[12] = swapIntoAlpha(px[12], shift);
    out_[13] = swapIntoAlpha(px[13], shift);
    out_[14] = swapIntoAlpha(px[14], shift);
    out_[15] = swapIntoAlpha(px[15], shift);
}

uint part2Map(uint p)
{
    uint bits = bc7fPart2Bitmask[p];
    uint map = 0;
    [unroll]
    for (uint i2 = 0; i2 < 16; ++i2)
        map |= ((bits >> i2) & 1u) << (i2 * 2u);
    return map;
}

uint part3Map(uint p) { return bc7fPart3Map[p]; }
uint subsetAt(uint map, uint i) { return (map >> (i * 2u)) & 3u; }

uint unpackByte(uint packed[16], uint index) { return (packed[index >> 2u] >> ((index & 3u) * 8u)) & 255u; }
uint anchorSecond(uint p) { return unpackByte(bc7fAnchorSecondPacked, p); }
uint anchorThird1(uint p) { return unpackByte(bc7fAnchorThird1Packed, p); }
uint anchorThird2(uint p) { return unpackByte(bc7fAnchorThird2Packed, p); }

void channelStats(uint px[16], out int4 sums, out int4 mins, out int4 maxes)
{
    sums = int4(0, 0, 0, 0);
    mins = int4(255, 255, 255, 255);
    maxes = int4(0, 0, 0, 0);
    [unroll]
    for (uint i3 = 0; i3 < 16; ++i3)
    {
        int4 t = unpack(px[i3]);
        sums += t;
        mins = min(mins, t);
        maxes = max(maxes, t);
    }
}

void covariance3(uint px[16], int3 mean, uint map, uint subset, out int icov[6])
{
    int rr = 0, rg = 0, rb = 0, gg = 0, gb = 0, bb = 0;
    [unroll]
    for (uint i4 = 0; i4 < 16; ++i4)
    {
        if (subsetAt(map, i4) != subset)
            continue;
        int3 d = unpack(px[i4]).rgb - mean;
        rr += d.r * d.r; rg += d.r * d.g; rb += d.r * d.b;
        gg += d.g * d.g; gb += d.g * d.b; bb += d.b * d.b;
    }
    icov[0] = rr; icov[1] = rg; icov[2] = rb; icov[3] = gg; icov[4] = gb; icov[5] = bb;
}

void covariance4(uint px[16], int4 mean, uint map, uint subset, out int icov[10])
{
    int rr = 0, rg = 0, rb = 0, ra = 0, gg = 0, gb = 0, ga = 0, bb = 0, ba = 0, aa = 0;
    [unroll]
    for (uint i5 = 0; i5 < 16; ++i5)
    {
        if (subsetAt(map, i5) != subset)
            continue;
        int4 d = unpack(px[i5]) - mean;
        rr += d.r * d.r; rg += d.r * d.g; rb += d.r * d.b; ra += d.r * d.a;
        gg += d.g * d.g; gb += d.g * d.b; ga += d.g * d.a;
        bb += d.b * d.b; ba += d.b * d.a; aa += d.a * d.a;
    }
    icov[0] = rr; icov[1] = rg; icov[2] = rb; icov[3] = ra; icov[4] = gg;
    icov[5] = gb; icov[6] = ga; icov[7] = bb; icov[8] = ba; icov[9] = aa;
}

int4 subsetSums(uint px[16], uint map, uint subset, out int count)
{
    int4 sums = int4(0, 0, 0, 0);
    count = 0;
    [unroll]
    for (uint i6 = 0; i6 < 16; ++i6)
        if (subsetAt(map, i6) == subset)
        {
            sums += unpack(px[i6]);
            count++;
        }
    return sums;
}

int3 scaledAxis3(float3 altX)
{
    int3 saxis = int3(306, 601, 117);
    float k = max(max(abs(altX.r), abs(altX.g)), abs(altX.b));
    if (abs(k) >= SmallFloatValue)
    {
        float m = 2048.f / k;
        saxis = int3(truncate(altX.r * m), truncate(altX.g * m), truncate(altX.b * m));
    }
    return int3(uint3(saxis) << 4);
}

int4 scaledAxis4(float4 altX)
{
    int4 saxis = int4(256, 256, 256, 256);
    float k = max(max(abs(altX.r), abs(altX.g)), max(abs(altX.b), abs(altX.a)));
    if (abs(k) >= SmallFloatValue)
    {
        float m = 2048.f / k;
        saxis = int4(truncate(altX.r * m), truncate(altX.g * m), truncate(altX.b * m), truncate(altX.a * m));
    }
    return int4(uint4(saxis) << 4);
}

// The texel index rides in the dot's free low four bits so ties break as the reference's min/max does.
// Returns texels rather than indices so nothing indexes px[] dynamically.
void findExtremes(uint px[16], int4 saxis, bool withAlpha, uint map, uint subset,
    out uint lowTexel, out uint highTexel)
{
    int lowDot = 0x7fffffff;
    int highDot = -0x7fffffff - 1;
    lowTexel = px[0];
    highTexel = px[0];
    [unroll]
    for (uint i7 = 0; i7 < 16; ++i7)
    {
        if (subsetAt(map, i7) != subset)
            continue;

        int4 t = unpack(px[i7]);
        int d = t.r * saxis.r + t.g * saxis.g + t.b * saxis.b + int(i7);
        if (withAlpha)
            d += t.a * saxis.a;

        if (d < lowDot) { lowDot = d; lowTexel = px[i7]; }
        if (d > highDot) { highDot = d; highTexel = px[i7]; }
    }
}

uint splitAlongAxis(uint px[16], float4 axis, int4 mean, bool withAlpha)
{
    uint bits = 0;
    [unroll]
    for (uint i8 = 0; i8 < 16; ++i8)
    {
        float4 d = float4(unpack(px[i8]) - mean);
        float projection = d.r * axis.r + d.g * axis.g + d.b * axis.b;
        if (withAlpha)
            projection += d.a * axis.a;
        if (projection > 0.f)
            bits |= 1u << i8;
    }
    return bits;
}

// One power iteration, as the reference's RGB paths do.
float3 principalAxis3(int icov[6], float scale)
{
    float3 w = scale * float3(icov[0], icov[3], icov[5]);
    return float3(
        icov[0] * w.x + icov[1] * w.y + icov[2] * w.z,
        icov[1] * w.x + icov[3] * w.y + icov[4] * w.z,
        icov[2] * w.x + icov[4] * w.y + icov[5] * w.z);
}

float4 iterateAxis4(int icov[10], float4 w)
{
    return float4(
        icov[0] * w.x + icov[1] * w.y + icov[2] * w.z + icov[3] * w.w,
        icov[1] * w.x + icov[4] * w.y + icov[5] * w.z + icov[6] * w.w,
        icov[2] * w.x + icov[5] * w.y + icov[7] * w.z + icov[8] * w.w,
        icov[3] * w.x + icov[6] * w.y + icov[8] * w.z + icov[9] * w.w);
}

float analyticalQuantEstimateSse(int endpointLevels, int weightLevels, int channels, int4 spans,
    float endpointWeightScale, int pixels)
{
    float dep = 1.f / (endpointLevels - 1);
    float dw = 1.f / (weightLevels - 1);
    float n = (float)weightLevels;
    float abSum = (2.f * n - 1.f) / (3.f * (n - 1.f));

    float pixelSse = endpointLevels == 256
        ? 0.f
        : dep * dep * ((1.f / 12.f) * abSum * (255.f * 255.f)) * channels * endpointWeightScale;

    float k = dw * dw * (1.f / 12.f);
    pixelSse += k * (spans.r * spans.r);
    pixelSse += k * (spans.g * spans.g);
    if (channels >= 3)
        pixelSse += k * (spans.b * spans.b);
    if (channels == 4)
        pixelSse += k * (spans.a * spans.a);

    return pixelSse * pixels;
}

float analyticalQuantEstimateSse1(int endpointLevels, int weightLevels, int span, float endpointWeightScale, int pixels)
{
    float dep = 1.f / (endpointLevels - 1);
    float dw = 1.f / (weightLevels - 1);
    float n = (float)weightLevels;
    float abSum = (2.f * n - 1.f) / (3.f * (n - 1.f));

    float pixelSse = endpointLevels == 256
        ? 0.f
        : dep * dep * ((1.f / 12.f) * abSum * (255.f * 255.f)) * endpointWeightScale;

    pixelSse += dw * dw * (1.f / 12.f) * (span * span);
    return pixelSse * pixels;
}

float estimateSlamToLineSse3D(int icov[6], float3 axis, out float orthoRatio)
{
    float totalVariance = icov[0] + icov[3] + icov[5];

    float l = sqrt(dot(axis, axis));
    axis = l < SmallFloatValue ? float3(.577350269f, .577350269f, .577350269f) : axis * (1.f / l);

    float3 x2 = float3(
        icov[0] * axis.x + icov[1] * axis.y + icov[2] * axis.z,
        icov[1] * axis.x + icov[3] * axis.y + icov[4] * axis.z,
        icov[2] * axis.x + icov[4] * axis.y + icov[5] * axis.z);

    float orthoVariance = max(0.f, totalVariance - dot(x2, axis));
    orthoRatio = totalVariance > SmallFloatValue ? orthoVariance / totalVariance : 0.f;
    return orthoVariance;
}

float estimateSlamToLineSse4D(int icov[10], float4 axis, out float orthoRatio)
{
    float totalVariance = icov[0] + icov[4] + icov[7] + icov[9];

    float l = sqrt(dot(axis, axis));
    axis = l < SmallFloatValue ? float4(.5f, .5f, .5f, .5f) : axis * (1.f / l);

    float4 x2 = iterateAxis4(icov, axis);
    float orthoVariance = max(0.f, totalVariance - dot(x2, axis));
    orthoRatio = totalVariance > SmallFloatValue ? orthoVariance / totalVariance : 0.f;
    return orthoVariance;
}

int4 expandEndpoint(int4 value, int totalBits)
{
    int4 shifted = (value << (8 - totalBits)) & 255;
    return shifted | (shifted >> totalBits);
}

float sumLanes(float4 v, int count)
{
    float sum = v.x + v.y + v.z;
    return count == 4 ? sum + v.w : sum;
}

// The error sum stays scalar so it accumulates in the reference's order and ties break identically.
void determinePBits(int totalComps, int compBits, float4 wantedLow, float4 wantedHigh, bool sharedPBit,
    out int4 bestMinColor, out int4 bestMaxColor, out uint2 bestPBits)
{
    int totalBits = compBits + 1;
    int iscalep = (1 << totalBits) - 1;
    float scalep = (float)iscalep;

    float bestError0 = NoCandidate;
    float bestError1 = NoCandidate;
    float bestError = NoCandidate;

    bestMinColor = int4(0, 0, 0, 0);
    bestMaxColor = int4(0, 0, 0, 0);
    bestPBits = uint2(0, 0);

    [unroll]
    for (int p9 = 0; p9 < 2; ++p9)
    {
        int4 minColor = min(max(int4((wantedLow * scalep - p9) * .5f + .5f) * 2 + p9, p9), iscalep - 1 + p9);
        int4 maxColor = min(max(int4((wantedHigh * scalep - p9) * .5f + .5f) * 2 + p9, p9), iscalep - 1 + p9);

        float4 scaledLow = float4(expandEndpoint(minColor, totalBits));
        float4 scaledHigh = float4(expandEndpoint(maxColor, totalBits));

        if (sharedPBit)
        {
            float4 deltaLow = scaledLow * (1.f / 255.f) - wantedLow;
            float4 deltaHigh = scaledHigh * (1.f / 255.f) - wantedHigh;
            float error = sumLanes(deltaLow * deltaLow + deltaHigh * deltaHigh, totalComps);
            if (error < bestError)
            {
                bestError = error;
                bestPBits = uint2(p9, p9);
                bestMinColor = minColor >> 1;
                bestMaxColor = maxColor >> 1;
            }
            continue;
        }

        float4 offsetLow = scaledLow - wantedLow * 255.f;
        float4 offsetHigh = scaledHigh - wantedHigh * 255.f;
        float error0 = sumLanes(offsetLow * offsetLow, totalComps);
        float error1 = sumLanes(offsetHigh * offsetHigh, totalComps);

        if (error0 < bestError0)
        {
            bestError0 = error0;
            bestPBits.x = uint(p9);
            bestMinColor = minColor >> 1;
        }
        if (error1 < bestError1)
        {
            bestError1 = error1;
            bestPBits.y = uint(p9);
            bestMaxColor = maxColor >> 1;
        }
    }
}

uint weightAt(uint2 w, uint i) { return ((i < 8u ? w.x : w.y) >> ((i & 7u) * 4u)) & 15u; }

struct SubsetLine
{
    int4 d;
    int offset;
    int denom;
    float scale;
};

SubsetLine makeLine(int4 lo, int4 hi, int mask, bool withAlpha)
{
    SubsetLine edge;
    edge.d = hi - lo;
    if (!withAlpha)
        edge.d.a = 0;
    edge.denom = edge.d.r * edge.d.r + edge.d.g * edge.d.g + edge.d.b * edge.d.b + edge.d.a * edge.d.a;
    edge.offset = lo.r * edge.d.r + lo.g * edge.d.g + lo.b * edge.d.b + lo.a * edge.d.a;
    edge.scale = edge.denom != 0 ? float(mask) / float(edge.denom) : 0.f;
    return edge;
}

uint2 evalWeights(uint px[16], SubsetLine a, SubsetLine b, SubsetLine c, uint map, int mask)
{
    uint2 w = uint2(0, 0);
    [unroll]
    for (uint i10 = 0; i10 < 16; ++i10)
    {
        uint s = subsetAt(map, i10);
        // Not a ternary: dxc's conditional operator rejects struct results.
        SubsetLine edge = a;
        if (s == 1u) edge = b; else if (s == 2u) edge = c;
        int4 t = unpack(px[i10]);
        int projection = t.r * edge.d.r + t.g * edge.d.g + t.b * edge.d.b + t.a * edge.d.a - edge.offset;
        uint sel = edge.denom == 0 ? (projection > 0 ? uint(mask) : 0u)
            : uint(clamp(int(float(projection) * edge.scale + .5f), 0, mask));
        if (i10 < 8u) w.x |= sel << (i10 * 4u); else w.y |= sel << ((i10 - 8u) * 4u);
    }
    return w;
}

uint2 evalWeightsSingle(uint px[16], int4 lo, int4 hi, int mask, bool withAlpha)
{
    SubsetLine edge = makeLine(lo, hi, mask, withAlpha);
    return evalWeights(px, edge, edge, edge, 0u, mask);
}

uint2 evalWeightsAlpha(uint px[16], int la, int ha, int mask)
{
    int da = ha - la;
    float scale = da != 0 ? float(mask) / float(da) : 0.f;

    uint2 w = uint2(0, 0);
    [unroll]
    for (uint i11 = 0; i11 < 16; ++i11)
    {
        int projection = unpack(px[i11]).a - la;
        uint sel = da == 0 ? (projection > 0 ? uint(mask) : 0u) : uint(clamp(int(float(projection) * scale + .5f), 0, mask));
        if (i11 < 8u) w.x |= sel << (i11 * 4u); else w.y |= sel << ((i11 - 8u) * 4u);
    }
    return w;
}

uint2 evalWeightsMode6Rgb(uint px[16], int3 lo, int3 hi, int p0, int p1)
{
    return evalWeightsSingle(px,
        int4(from7p(lo.r, p0), from7p(lo.g, p0), from7p(lo.b, p0), 0),
        int4(from7p(hi.r, p1), from7p(hi.g, p1), from7p(hi.b, p1), 0), 15, false);
}

uint2 evalWeightsMode6Rgba(uint px[16], int4 lo, int4 hi, int p0, int p1)
{
    return evalWeightsSingle(px,
        int4(from7p(lo.r, p0), from7p(lo.g, p0), from7p(lo.b, p0), from7p(lo.a, p0)),
        int4(from7p(hi.r, p1), from7p(hi.g, p1), from7p(hi.b, p1), from7p(hi.a, p1)), 15, true);
}

// Identical float math to Bc7BlockEncoder's BuildLsTable, computed per weight so no array is indexed at runtime.
bool leastSquares(uint px[16], uint2 weights, uint weightMask, float4 totals, bool withAlpha,
    out float4 xl, out float4 xh)
{
    float z00 = 0, z10 = 0, z11 = 0;
    float4 q00 = float4(0, 0, 0, 0);

    [unroll]
    for (uint i12 = 0; i12 < 16; ++i12)
    {
        float w = weightFor(weightAt(weights, i12), weightMask) * (1.f / 64.f);
        z00 += w * w;
        z10 += (1.f - w) * w;
        z11 += (1.f - w) * (1.f - w);
        q00 += w * float4(unpack(px[i12]));
    }

    float4 q10 = totals - q00;

    xl = float4(0, 0, 0, 0);
    xh = float4(0, 0, 0, 0);
    float4 iz;
    if (!invert2x2(z00, z10, z11, iz))
        return false;

    xh = clamp(iz.x * q00 + iz.y * q10, 0.f, 255.f);
    xl = clamp(iz.z * q00 + iz.w * q10, 0.f, 255.f);
    if (!withAlpha)
    {
        xh.a = 0;
        xl.a = 0;
    }
    return true;
}

bool leastSquaresAlpha(uint px[16], uint2 weights, uint weightMask, float totalA, out float xl, out float xh)
{
    float z00 = 0, z10 = 0, z11 = 0, q00 = 0;

    [unroll]
    for (uint i13 = 0; i13 < 16; ++i13)
    {
        float w = weightFor(weightAt(weights, i13), weightMask) * (1.f / 64.f);
        z00 += w * w;
        z10 += (1.f - w) * w;
        z11 += (1.f - w) * (1.f - w);
        q00 += w * unpack(px[i13]).a;
    }

    xl = 0;
    xh = 0;
    float4 iz;
    if (!invert2x2(z00, z10, z11, iz))
        return false;

    float q10 = totalA - q00;
    xh = clamp(iz.x * q00 + iz.y * q10, 0.f, 255.f);
    xl = clamp(iz.z * q00 + iz.w * q10, 0.f, 255.f);
    return true;
}

// Unclamped, as the reference's per-subset solve leaves them.
bool leastSquaresSubset(uint px[16], uint2 weights, uint weightMask, uint map, uint subset,
    float4 totals, out float4 xl, out float4 xh)
{
    float z00 = 0, z10 = 0, z11 = 0;
    float4 q00 = float4(0, 0, 0, 0);

    [unroll]
    for (uint i14 = 0; i14 < 16; ++i14)
    {
        if (subsetAt(map, i14) != subset)
            continue;
        float w = weightFor(weightAt(weights, i14), weightMask) * (1.f / 64.f);
        z00 += w * w;
        z10 += (1.f - w) * w;
        z11 += (1.f - w) * (1.f - w);
        q00 += w * float4(unpack(px[i14]));
    }

    xl = float4(0, 0, 0, 0);
    xh = float4(0, 0, 0, 0);
    float4 iz;
    if (!invert2x2(z00, z10, z11, iz))
        return false;

    float4 q10 = totals - q00;
    xh = iz.x * q00 + iz.y * q10;
    xl = iz.z * q00 + iz.w * q10;
    return true;
}

// count <= 24; the field may straddle a word. Every callsite passes a constant offset.
void putBits(inout uint4 block, uint offset, uint count, uint value)
{
    uint w = offset >> 5u;
    uint b = offset & 31u;
    uint v = value & ((1u << count) - 1u);
    block[w] |= v << b;
    if (b + count > 32u)
        block[w + 1u] |= v >> (32u - b);
}

// Pure integer, so byte-identical to Bc7BlockEncoder.PackMode5Solid.
uint4 packMode5Solid(uint texel)
{
    int4 c = unpack(texel);
    uint r = bc7fMode5Optimal[c.r];
    uint g = bc7fMode5Optimal[c.g];
    uint b = bc7fMode5Optimal[c.b];

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 6, 32u);
    putBits(block, 8, 7, r & 255u);
    putBits(block, 15, 7, r >> 8u);
    putBits(block, 22, 7, g & 255u);
    putBits(block, 29, 7, g >> 8u);
    putBits(block, 36, 7, b & 255u);
    putBits(block, 43, 7, b >> 8u);
    putBits(block, 50, 8, uint(c.a));
    putBits(block, 58, 8, uint(c.a));
    // Every colour selector is 1 and every alpha selector 0.
    block.z |= 0xaaaaaaacu;
    return block;
}

uint4 encodeMode6RgbaBlock(int4 lo, int p0, int4 hi, int p1, uint2 weights)
{
    uint weightInv = 0;
    if ((weightAt(weights, 0) & 8u) != 0u)
    {
        int4 t = lo; lo = hi; hi = t;
        int tp = p0; p0 = p1; p1 = tp;
        weightInv = 15u;
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 7, 64u);
    putBits(block, 7, 7, uint(lo.r));
    putBits(block, 14, 7, uint(hi.r));
    putBits(block, 21, 7, uint(lo.g));
    putBits(block, 28, 7, uint(hi.g));
    putBits(block, 35, 7, uint(lo.b));
    putBits(block, 42, 7, uint(hi.b));
    putBits(block, 49, 7, uint(lo.a));
    putBits(block, 56, 7, uint(hi.a));
    putBits(block, 63, 1, uint(p0));
    putBits(block, 64, 1, uint(p1));

    // Weight 0 is one bit narrower: BC7 stores the anchor's high bit implicitly, which the swap above paid for.
    putBits(block, 65, 3, weightAt(weights, 0) ^ weightInv);
    [unroll]
    for (uint i15 = 1; i15 < 16; ++i15)
        putBits(block, 64u + i15 * 4u, 4, weightAt(weights, i15) ^ weightInv);

    return block;
}

uint4 encodeMode5RgbaBlock(int4 lo, int4 hi, uint2 colorWeights, uint2 alphaWeights, uint rotation)
{
    uint colorInv = 0, alphaInv = 0;
    if ((weightAt(colorWeights, 0) & 2u) != 0u)
    {
        int3 t = lo.rgb; lo.rgb = hi.rgb; hi.rgb = t;
        colorInv = 3u;
    }
    if ((weightAt(alphaWeights, 0) & 2u) != 0u)
    {
        int t = lo.a; lo.a = hi.a; hi.a = t;
        alphaInv = 3u;
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 6, 32u);
    putBits(block, 6, 2, rotation);
    putBits(block, 8, 7, uint(lo.r));
    putBits(block, 15, 7, uint(hi.r));
    putBits(block, 22, 7, uint(lo.g));
    putBits(block, 29, 7, uint(hi.g));
    putBits(block, 36, 7, uint(lo.b));
    putBits(block, 43, 7, uint(hi.b));
    putBits(block, 50, 8, uint(lo.a));
    putBits(block, 58, 8, uint(hi.a));

    uint offset = 66;
    putBits(block, offset, 1, weightAt(colorWeights, 0) ^ colorInv);
    offset += 1;
    [unroll]
    for (uint i16 = 1; i16 < 16; ++i16)
    {
        putBits(block, offset, 2, weightAt(colorWeights, i16) ^ colorInv);
        offset += 2;
    }
    putBits(block, offset, 1, weightAt(alphaWeights, 0) ^ alphaInv);
    offset += 1;
    [unroll]
    for (uint i17 = 1; i17 < 16; ++i17)
    {
        putBits(block, offset, 2, weightAt(alphaWeights, i17) ^ alphaInv);
        offset += 2;
    }
    return block;
}

uint4 encodeMode4RgbaBlock(int4 lo, int4 hi, uint2 weights0, uint2 weights1, uint rotation, uint indexFlag)
{
    uint2 twoBitWeights = indexFlag != 0u ? weights1 : weights0;
    uint2 threeBitWeights = indexFlag != 0u ? weights0 : weights1;

    uint threeBitInv = 0, twoBitInv = 0;
    if ((weightAt(threeBitWeights, 0) & 4u) != 0u)
    {
        threeBitInv = 7u;
        if (indexFlag != 0u) { int3 t = lo.rgb; lo.rgb = hi.rgb; hi.rgb = t; }
        else { int t = lo.a; lo.a = hi.a; hi.a = t; }
    }
    if ((weightAt(twoBitWeights, 0) & 2u) != 0u)
    {
        twoBitInv = 3u;
        if (indexFlag != 0u) { int t = lo.a; lo.a = hi.a; hi.a = t; }
        else { int3 t = lo.rgb; lo.rgb = hi.rgb; hi.rgb = t; }
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 5, 16u);
    putBits(block, 5, 2, rotation);
    putBits(block, 7, 1, indexFlag);
    putBits(block, 8, 5, uint(lo.r));
    putBits(block, 13, 5, uint(hi.r));
    putBits(block, 18, 5, uint(lo.g));
    putBits(block, 23, 5, uint(hi.g));
    putBits(block, 28, 5, uint(lo.b));
    putBits(block, 33, 5, uint(hi.b));
    putBits(block, 38, 6, uint(lo.a));
    putBits(block, 44, 6, uint(hi.a));

    uint offset = 50;
    putBits(block, offset, 1, weightAt(twoBitWeights, 0) ^ twoBitInv);
    offset += 1;
    [unroll]
    for (uint i18 = 1; i18 < 16; ++i18)
    {
        putBits(block, offset, 2, weightAt(twoBitWeights, i18) ^ twoBitInv);
        offset += 2;
    }
    putBits(block, offset, 2, weightAt(threeBitWeights, 0) ^ threeBitInv);
    offset += 2;
    [unroll]
    for (uint i19 = 1; i19 < 16; ++i19)
    {
        putBits(block, offset, 3, weightAt(threeBitWeights, i19) ^ threeBitInv);
        offset += 3;
    }
    return block;
}

void putPartitionedWeights(inout uint4 block, uint offset, uint bits, uint2 weights, uint map,
    uint3 weightInv, uint anchor0, uint anchor1)
{
    [unroll]
    for (uint i20 = 0; i20 < 16; ++i20)
    {
        uint s = subsetAt(map, i20);
        uint inv = s == 0u ? weightInv.x : (s == 1u ? weightInv.y : weightInv.z);
        bool isAnchor = i20 == 0u || i20 == anchor0 || i20 == anchor1;
        putBits(block, offset, isAnchor ? bits - 1u : bits, weightAt(weights, i20) ^ inv);
        offset += isAnchor ? bits - 1u : bits;
    }
}

uint4 encodeMode1RgbBlock(uint partId, int3 lo[2], int3 hi[2], uint2 pbits, uint2 weights, uint map)
{
    uint anchor = anchorSecond(partId);
    uint3 weightInv = uint3(0, 0, 0);

    if ((weightAt(weights, 0) & 4u) != 0u)
    {
        int3 t = lo[0]; lo[0] = hi[0]; hi[0] = t;
        weightInv.x = 7u;
    }
    if ((weightAt(weights, anchor) & 4u) != 0u)
    {
        int3 t = lo[1]; lo[1] = hi[1]; hi[1] = t;
        weightInv.y = 7u;
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 2, 2u);
    putBits(block, 2, 6, partId);
    uint offset = 8;
    [unroll]
    for (uint c21 = 0; c21 < 3; ++c21)
    {
        putBits(block, offset, 6, uint(lo[0][c21])); offset += 6;
        putBits(block, offset, 6, uint(hi[0][c21])); offset += 6;
        putBits(block, offset, 6, uint(lo[1][c21])); offset += 6;
        putBits(block, offset, 6, uint(hi[1][c21])); offset += 6;
    }
    putBits(block, offset, 1, pbits.x); offset += 1;
    putBits(block, offset, 1, pbits.y); offset += 1;
    putPartitionedWeights(block, offset, 3, weights, map, weightInv, anchor, anchor);
    return block;
}

uint4 encodeMode3RgbBlock(uint partId, int3 lo[2], int3 hi[2], uint4 pbits, uint2 weights, uint map)
{
    uint anchor = anchorSecond(partId);
    uint3 weightInv = uint3(0, 0, 0);

    if ((weightAt(weights, 0) & 2u) != 0u)
    {
        int3 t = lo[0]; lo[0] = hi[0]; hi[0] = t;
        uint tp = pbits.x; pbits.x = pbits.y; pbits.y = tp;
        weightInv.x = 3u;
    }
    if ((weightAt(weights, anchor) & 2u) != 0u)
    {
        int3 t = lo[1]; lo[1] = hi[1]; hi[1] = t;
        uint tp = pbits.z; pbits.z = pbits.w; pbits.w = tp;
        weightInv.y = 3u;
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 4, 8u);
    putBits(block, 4, 6, partId);
    uint offset = 10;
    [unroll]
    for (uint c22 = 0; c22 < 3; ++c22)
    {
        putBits(block, offset, 7, uint(lo[0][c22])); offset += 7;
        putBits(block, offset, 7, uint(hi[0][c22])); offset += 7;
        putBits(block, offset, 7, uint(lo[1][c22])); offset += 7;
        putBits(block, offset, 7, uint(hi[1][c22])); offset += 7;
    }
    putBits(block, offset, 1, pbits.x); offset += 1;
    putBits(block, offset, 1, pbits.y); offset += 1;
    putBits(block, offset, 1, pbits.z); offset += 1;
    putBits(block, offset, 1, pbits.w); offset += 1;
    putPartitionedWeights(block, offset, 2, weights, map, weightInv, anchor, anchor);
    return block;
}

uint4 encodeMode7RgbaBlock(uint partId, int4 lo[2], int4 hi[2], uint4 pbits, uint2 weights, uint map)
{
    uint anchor = anchorSecond(partId);
    uint3 weightInv = uint3(0, 0, 0);

    if ((weightAt(weights, 0) & 2u) != 0u)
    {
        int4 t = lo[0]; lo[0] = hi[0]; hi[0] = t;
        uint tp = pbits.x; pbits.x = pbits.y; pbits.y = tp;
        weightInv.x = 3u;
    }
    if ((weightAt(weights, anchor) & 2u) != 0u)
    {
        int4 t = lo[1]; lo[1] = hi[1]; hi[1] = t;
        uint tp = pbits.z; pbits.z = pbits.w; pbits.w = tp;
        weightInv.y = 3u;
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 8, 128u);
    putBits(block, 8, 6, partId);
    uint offset = 14;
    [unroll]
    for (uint c23 = 0; c23 < 4; ++c23)
    {
        putBits(block, offset, 5, uint(lo[0][c23])); offset += 5;
        putBits(block, offset, 5, uint(hi[0][c23])); offset += 5;
        putBits(block, offset, 5, uint(lo[1][c23])); offset += 5;
        putBits(block, offset, 5, uint(hi[1][c23])); offset += 5;
    }
    putBits(block, offset, 1, pbits.x); offset += 1;
    putBits(block, offset, 1, pbits.y); offset += 1;
    putBits(block, offset, 1, pbits.z); offset += 1;
    putBits(block, offset, 1, pbits.w); offset += 1;
    putPartitionedWeights(block, offset, 2, weights, map, weightInv, anchor, anchor);
    return block;
}

uint4 encodeMode0RgbBlock(uint partId, int3 lo[3], int3 hi[3], uint pbits[6], uint2 weights, uint map)
{
    uint anchor0 = anchorThird1(partId);
    uint anchor1 = anchorThird2(partId);
    uint3 weightInv = uint3(0, 0, 0);

    [unroll]
    for (uint s24 = 0; s24 < 3; ++s24)
    {
        uint at = s24 == 0u ? 0u : (s24 == 1u ? anchor0 : anchor1);
        if ((weightAt(weights, at) & 4u) == 0u)
            continue;

        int3 t = lo[s24]; lo[s24] = hi[s24]; hi[s24] = t;
        uint tp = pbits[s24 * 2]; pbits[s24 * 2] = pbits[s24 * 2 + 1]; pbits[s24 * 2 + 1] = tp;
        if (s24 == 0u) weightInv.x = 7u; else if (s24 == 1u) weightInv.y = 7u; else weightInv.z = 7u;
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 1, 1u);
    putBits(block, 1, 4, partId);
    uint offset = 5;
    [unroll]
    for (uint c25 = 0; c25 < 3; ++c25)
        [unroll]
        for (uint s26 = 0; s26 < 3; ++s26)
        {
            putBits(block, offset, 4, uint(lo[s26][c25])); offset += 4;
            putBits(block, offset, 4, uint(hi[s26][c25])); offset += 4;
        }
    [unroll]
    for (uint i27 = 0; i27 < 6; ++i27)
    {
        putBits(block, offset, 1, pbits[i27]);
        offset += 1;
    }
    putPartitionedWeights(block, offset, 3, weights, map, weightInv, anchor0, anchor1);
    return block;
}

uint4 encodeMode2RgbBlock(uint partId, int3 lo[3], int3 hi[3], uint2 weights, uint map)
{
    uint anchor0 = anchorThird1(partId);
    uint anchor1 = anchorThird2(partId);
    uint3 weightInv = uint3(0, 0, 0);

    [unroll]
    for (uint s28 = 0; s28 < 3; ++s28)
    {
        uint at = s28 == 0u ? 0u : (s28 == 1u ? anchor0 : anchor1);
        if ((weightAt(weights, at) & 2u) == 0u)
            continue;

        int3 t = lo[s28]; lo[s28] = hi[s28]; hi[s28] = t;
        if (s28 == 0u) weightInv.x = 3u; else if (s28 == 1u) weightInv.y = 3u; else weightInv.z = 3u;
    }

    uint4 block = uint4(0, 0, 0, 0);
    putBits(block, 0, 3, 4u);
    putBits(block, 3, 6, partId);
    uint offset = 9;
    [unroll]
    for (uint c29 = 0; c29 < 3; ++c29)
        [unroll]
        for (uint s30 = 0; s30 < 3; ++s30)
        {
            putBits(block, offset, 5, uint(lo[s30][c29])); offset += 5;
            putBits(block, offset, 5, uint(hi[s30][c29])); offset += 5;
        }
    putPartitionedWeights(block, offset, 2, weights, map, weightInv, anchor0, anchor1);
    return block;
}

// The pattern id in the score's low byte makes ties fall to the lowest id, as the scalar scan does.
uint bestPattern2(uint desiredPatBits)
{
    uint best = 0xffffffffu;
    for (uint p31 = 0; p31 < 64u; ++p31)
    {
        uint diff = countbits(bc7fPart2Bitmask[p31] ^ desiredPatBits);
        uint score = (min(diff, 16u - diff) << 8u) | p31;
        best = min(best, score);
    }
    return best & 255u;
}

// Scored over all six subset permutations since a partition's subset numbering is arbitrary. `first16` is
// the best of the sixteen patterns mode 0 can name.
uint bestPattern3(uint desiredMap, out uint first16)
{
    uint m0 = 0, m1 = 0, m2 = 0;
    [unroll]
    for (uint i32 = 0; i32 < 16; ++i32)
    {
        uint s = subsetAt(desiredMap, i32);
        if (s == 0u) m0 |= 1u << i32; else if (s == 1u) m1 |= 1u << i32; else m2 |= 1u << i32;
    }

    int n0 = int(countbits(m0));
    int n1 = int(countbits(m1));
    int n2 = 16 - n0 - n1;

    int bestScore = -1;
    uint bestPattern = 0;
    first16 = 0;

    for (uint p33 = 0; p33 < 64u; ++p33)
    {
        uint s0Mask = bc7fPart3Bitmask[p33] & 0xffffu;
        uint s1Mask = bc7fPart3Bitmask[p33] >> 16u;

        int c00 = int(countbits(m0 & s0Mask));
        int c01 = int(countbits(m0 & s1Mask));
        int c02 = n0 - c00 - c01;
        int c10 = int(countbits(m1 & s0Mask));
        int c11 = int(countbits(m1 & s1Mask));
        int c12 = n1 - c10 - c11;
        int c20 = int(countbits(m2 & s0Mask));
        int c21 = int(countbits(m2 & s1Mask));
        int c22 = n2 - c20 - c21;

        int s = c00 + c11 + c22;
        s = max(s, c00 + c12 + c21);
        s = max(s, c01 + c10 + c22);
        s = max(s, c01 + c12 + c20);
        s = max(s, c02 + c10 + c21);
        s = max(s, c02 + c11 + c20);

        if (s > bestScore)
        {
            bestScore = s;
            bestPattern = p33;
            if (s == 16)
            {
                if (p33 <= 15u)
                    first16 = bestPattern;
                break;
            }
        }

        if (p33 == 15u)
            first16 = bestPattern;
    }

    return bestPattern;
}

struct Candidate
{
    bool ok;
    uint4 block;
    float sse;
};

Candidate makeCandidate(uint4 block, float sse)
{
    Candidate c;
    c.ok = true;
    c.block = block;
    c.sse = sse;
    return c;
}

Candidate packMode4Or5(uint original[16], uint dpChannel, float sseEstimateToBeat)
{
    Candidate result;
    result.ok = false;
    result.block = uint4(0, 0, 0, 0);
    result.sse = NoCandidate;

    uint px[16];
    rotateChannelIntoAlpha(original, dpChannel, px);

    int4 sums, mins, maxes;
    channelStats(px, sums, mins, maxes);
    int minA = mins.a;
    int maxA = maxes.a;
    int3 mean = (sums.rgb + 8) >> 4;

    int icov[6];
    covariance3(px, mean, 0u, 0u, icov);
    int blockMaxVar = max(max(icov[0], icov[3]), icov[5]);
    float3 altX = principalAxis3(icov, blockMaxVar != 0 ? 1.f / blockMaxVar : 0.f);

    float ignored;
    float rgbSlamToLineSse = estimateSlamToLineSse3D(icov, altX, ignored);

    uint lowTexel, highTexel;
    findExtremes(px, int4(scaledAxis3(altX), 0), false, 0u, 0u, lowTexel, highTexel);
    int4 low = unpack(lowTexel);
    int4 high = unpack(highTexel);

    int4 rgbSpans = int4(high.rgb - low.rgb, 0);
    int alphaSpan = maxA - minA;

    float mode4Rgb3Alpha2 = rgbSlamToLineSse
        + analyticalQuantEstimateSse(32, 8, 3, rgbSpans, 1.f, 16)
        + analyticalQuantEstimateSse1(64, 4, alphaSpan, 1.f, 16);
    float mode4Rgb2Alpha3 = rgbSlamToLineSse
        + analyticalQuantEstimateSse(32, 4, 3, rgbSpans, 1.f, 16)
        + analyticalQuantEstimateSse1(64, 8, alphaSpan, 1.f, 16);
    float mode5Total = rgbSlamToLineSse
        + analyticalQuantEstimateSse(128, 4, 3, rgbSpans, 1.f, 16)
        + analyticalQuantEstimateSse1(256, 4, alphaSpan, 1.f, 16);

    uint rotation = (dpChannel + 1u) & 3u;
    float4 totals = float4(sums);

    if (mode5Total < min(mode4Rgb3Alpha2, mode4Rgb2Alpha3))
    {
        result.sse = mode5Total;
        if (mode5Total >= sseEstimateToBeat)
            return result;

        int3 lo = int3(to7(low.r), to7(low.g), to7(low.b));
        int3 hi = int3(to7(high.r), to7(high.g), to7(high.b));
        int la = minA, ha = maxA;

        uint2 colorWeights = evalWeightsSingle(px,
            int4(from7(lo.r), from7(lo.g), from7(lo.b), 0),
            int4(from7(hi.r), from7(hi.g), from7(hi.b), 0), 3, false);

        float4 xl, xh;
        if (leastSquares(px, colorWeights, 3u, float4(totals.rgb, 0), false, xl, xh))
        {
            lo = int3(roundHalfAway(xl.r * (127.f / 255.f)), roundHalfAway(xl.g * (127.f / 255.f)), roundHalfAway(xl.b * (127.f / 255.f)));
            hi = int3(roundHalfAway(xh.r * (127.f / 255.f)), roundHalfAway(xh.g * (127.f / 255.f)), roundHalfAway(xh.b * (127.f / 255.f)));
            colorWeights = evalWeightsSingle(px,
                int4(from7(lo.r), from7(lo.g), from7(lo.b), 0),
                int4(from7(hi.r), from7(hi.g), from7(hi.b), 0), 3, false);
        }

        uint2 alphaWeights = evalWeightsAlpha(px, la, ha, 3);
        float nal, nah;
        if (leastSquaresAlpha(px, alphaWeights, 3u, totals.a, nal, nah))
        {
            la = roundHalfAway(nal);
            ha = roundHalfAway(nah);
            alphaWeights = evalWeightsAlpha(px, la, ha, 3);
        }

        result.block = encodeMode5RgbaBlock(int4(lo, la), int4(hi, ha), colorWeights, alphaWeights, rotation);
        result.ok = true;
        return result;
    }

    bool rgb3Alpha2 = mode4Rgb3Alpha2 < mode4Rgb2Alpha3;
    result.sse = rgb3Alpha2 ? mode4Rgb3Alpha2 : mode4Rgb2Alpha3;
    if (result.sse >= sseEstimateToBeat)
        return result;

    int rgbMask = rgb3Alpha2 ? 7 : 3;
    int alphaMask = rgb3Alpha2 ? 3 : 7;

    int3 lo = int3(to5(low.r), to5(low.g), to5(low.b));
    int3 hi = int3(to5(high.r), to5(high.g), to5(high.b));
    int la = to6(minA), ha = to6(maxA);

    uint2 colorWeights = evalWeightsSingle(px,
        int4(from5(lo.r), from5(lo.g), from5(lo.b), 0),
        int4(from5(hi.r), from5(hi.g), from5(hi.b), 0), rgbMask, false);

    float4 xl, xh;
    if (leastSquares(px, colorWeights, uint(rgbMask), float4(totals.rgb, 0), false, xl, xh))
    {
        lo = int3(roundHalfAway(xl.r * (31.f / 255.f)), roundHalfAway(xl.g * (31.f / 255.f)), roundHalfAway(xl.b * (31.f / 255.f)));
        hi = int3(roundHalfAway(xh.r * (31.f / 255.f)), roundHalfAway(xh.g * (31.f / 255.f)), roundHalfAway(xh.b * (31.f / 255.f)));
        colorWeights = evalWeightsSingle(px,
            int4(from5(lo.r), from5(lo.g), from5(lo.b), 0),
            int4(from5(hi.r), from5(hi.g), from5(hi.b), 0), rgbMask, false);
    }

    uint2 alphaWeights = evalWeightsAlpha(px, from6(la), from6(ha), alphaMask);

    float nal, nah;
    if (leastSquaresAlpha(px, alphaWeights, uint(alphaMask), totals.a, nal, nah))
    {
        la = roundHalfAway(nal * (63.f / 255.f));
        ha = roundHalfAway(nah * (63.f / 255.f));
        alphaWeights = evalWeightsAlpha(px, from6(la), from6(ha), alphaMask);
    }

    result.block = encodeMode4RgbaBlock(int4(lo, la), int4(hi, ha), colorWeights, alphaWeights,
        rotation, rgb3Alpha2 ? 1u : 0u);
    result.ok = true;
    return result;
}

Candidate packMode1Or3Rgb(uint px[16], float3 blockAxis, int3 blockMean, float sseEstimateToBeat, uint flags)
{
    Candidate result;
    result.ok = false;
    result.block = uint4(0, 0, 0, 0);
    result.sse = NoCandidate;

    uint partId = bestPattern2(splitAlongAxis(px, float4(blockAxis, 0), int4(blockMean, 0), false));
    uint map = part2Map(partId);

    int4 subsetTotals[2];
    int subsetCount[2];
    int4 subsetLow[2];
    int4 subsetHigh[2];
    float slamToLineSse = 0.f;

    [unroll]
    for (uint s34 = 0; s34 < 2; ++s34)
    {
        subsetTotals[s34] = subsetSums(px, map, s34, subsetCount[s34]);
        int t = subsetCount[s34];
        int3 mean = (subsetTotals[s34].rgb + (t >> 1)) / t;

        int icov[6];
        covariance3(px, mean, map, s34, icov);
        int blockMaxVar = max(max(icov[0], icov[3]), icov[5]);
        float3 altX = principalAxis3(icov, 1.f / (blockMaxVar + .0000125f));

        float ignored;
        slamToLineSse += estimateSlamToLineSse3D(icov, altX, ignored);

        uint lowTexel, highTexel;
        findExtremes(px, int4(scaledAxis3(altX), 0), false, map, s34, lowTexel, highTexel);
        subsetLow[s34] = unpack(lowTexel);
        subsetHigh[s34] = unpack(highTexel);
    }

    bool pbitOpt = (flags & FLAG_PBIT_OPT) != 0u;
    float mode1Estimate = 0, mode3Estimate = 0;
    [unroll]
    for (uint s35 = 0; s35 < 2; ++s35)
    {
        int4 spans = int4(subsetHigh[s35].rgb - subsetLow[s35].rgb, 0);
        mode1Estimate += analyticalQuantEstimateSse(64, 8, 3, spans, pbitOpt ? UniquePBitDiscount : 1.f, subsetCount[s35]);
        mode3Estimate += analyticalQuantEstimateSse(128, 4, 3, spans, pbitOpt ? SharedPBitDiscount : 1.f, subsetCount[s35]);
    }

    float totalMode1Sse = slamToLineSse + mode1Estimate;
    float totalMode3Sse = slamToLineSse + mode3Estimate;

    int3 lo[2];
    int3 hi[2];

    if (totalMode1Sse < totalMode3Sse)
    {
        result.sse = totalMode1Sse;
        if (totalMode1Sse >= sseEstimateToBeat)
            return result;

        uint2 pbits = uint2(0, 0);
        [unroll]
        for (uint s36 = 0; s36 < 2; ++s36)
        {
            if (pbitOpt)
            {
                int4 minColor, maxColor;
                uint2 pair;
                determinePBits(3, 6, float4(subsetLow[s36].rgb, 0) * (1.f / 255.f),
                    float4(subsetHigh[s36].rgb, 0) * (1.f / 255.f), true, minColor, maxColor, pair);
                pbits[s36] = pair.x;
                lo[s36] = minColor.rgb;
                hi[s36] = maxColor.rgb;
            }
            else
            {
                int l = subsetLow[s36].r + subsetLow[s36].g + subsetLow[s36].b;
                int h = subsetHigh[s36].r + subsetHigh[s36].g + subsetHigh[s36].b;
                pbits[s36] = max(l, h) >= 129 * 3 ? 1u : 0u;
                int p = int(pbits[s36]);
                lo[s36] = int3(to6p(subsetLow[s36].r, p), to6p(subsetLow[s36].g, p), to6p(subsetLow[s36].b, p));
                hi[s36] = int3(to6p(subsetHigh[s36].r, p), to6p(subsetHigh[s36].g, p), to6p(subsetHigh[s36].b, p));
            }
        }

        [unroll]
        for (uint pass37 = 0; pass37 < 2; ++pass37)
        {
            SubsetLine line0 = makeLine(
                int4(from6(lo[0].r, int(pbits.x)), from6(lo[0].g, int(pbits.x)), from6(lo[0].b, int(pbits.x)), 0),
                int4(from6(hi[0].r, int(pbits.x)), from6(hi[0].g, int(pbits.x)), from6(hi[0].b, int(pbits.x)), 0), 7, false);
            SubsetLine line1 = makeLine(
                int4(from6(lo[1].r, int(pbits.y)), from6(lo[1].g, int(pbits.y)), from6(lo[1].b, int(pbits.y)), 0),
                int4(from6(hi[1].r, int(pbits.y)), from6(hi[1].g, int(pbits.y)), from6(hi[1].b, int(pbits.y)), 0), 7, false);
            uint2 weights = evalWeights(px, line0, line1, line0, map, 7);

            if (pass37 == 1)
                return makeCandidate(encodeMode1RgbBlock(partId, lo, hi, pbits, weights, map), totalMode1Sse);

            [unroll]
            for (uint s38 = 0; s38 < 2; ++s38)
            {
                float4 xl, xh;
                if (!leastSquaresSubset(px, weights, 7u, map, s38, float4(subsetTotals[s38].rgb, 0), xl, xh))
                    continue;

                if (pbitOpt)
                {
                    int4 minColor, maxColor;
                    uint2 pair;
                    determinePBits(3, 6, float4(clamp(xl.rgb * (1.f / 255.f), 0.f, 1.f), 0),
                        float4(clamp(xh.rgb * (1.f / 255.f), 0.f, 1.f), 0), true, minColor, maxColor, pair);
                    pbits[s38] = pair.x;
                    lo[s38] = minColor.rgb;
                    hi[s38] = maxColor.rgb;
                }
                else
                {
                    float l = xl.r + xl.g + xl.b;
                    float h = xh.r + xh.g + xh.b;
                    pbits[s38] = max(l, h) >= 129.f * 3.f ? 1u : 0u;
                    int p = int(pbits[s38]);
                    lo[s38] = int3(to6pClamp(xl.r, p), to6pClamp(xl.g, p), to6pClamp(xl.b, p));
                    hi[s38] = int3(to6pClamp(xh.r, p), to6pClamp(xh.g, p), to6pClamp(xh.b, p));
                }
            }
        }
        return result;
    }

    result.sse = totalMode3Sse;
    if (totalMode3Sse >= sseEstimateToBeat)
        return result;

    uint4 pbits = uint4(0, 0, 0, 0);
    [unroll]
    for (uint s39 = 0; s39 < 2; ++s39)
    {
        if (pbitOpt)
        {
            int4 minColor, maxColor;
            uint2 pair;
            determinePBits(3, 7, float4(subsetLow[s39].rgb, 0) * (1.f / 255.f),
                float4(subsetHigh[s39].rgb, 0) * (1.f / 255.f), false, minColor, maxColor, pair);
            pbits[s39 * 2] = pair.x;
            pbits[s39 * 2 + 1] = pair.y;
            lo[s39] = minColor.rgb;
            hi[s39] = maxColor.rgb;
        }
        else
        {
            int lowPBit = subsetLow[s39].r + subsetLow[s39].g + subsetLow[s39].b >= 129 ? 1 : 0;
            int highPBit = subsetHigh[s39].r + subsetHigh[s39].g + subsetHigh[s39].b >= 129 ? 1 : 0;
            pbits[s39 * 2] = uint(lowPBit);
            pbits[s39 * 2 + 1] = uint(highPBit);
            lo[s39] = int3(to7p(subsetLow[s39].r, lowPBit), to7p(subsetLow[s39].g, lowPBit), to7p(subsetLow[s39].b, lowPBit));
            hi[s39] = int3(to7p(subsetHigh[s39].r, highPBit), to7p(subsetHigh[s39].g, highPBit), to7p(subsetHigh[s39].b, highPBit));
        }
    }

    [unroll]
    for (uint pass40 = 0; pass40 < 2; ++pass40)
    {
        SubsetLine line0 = makeLine(
            int4(from7p(lo[0].r, int(pbits.x)), from7p(lo[0].g, int(pbits.x)), from7p(lo[0].b, int(pbits.x)), 0),
            int4(from7p(hi[0].r, int(pbits.y)), from7p(hi[0].g, int(pbits.y)), from7p(hi[0].b, int(pbits.y)), 0), 3, false);
        SubsetLine line1 = makeLine(
            int4(from7p(lo[1].r, int(pbits.z)), from7p(lo[1].g, int(pbits.z)), from7p(lo[1].b, int(pbits.z)), 0),
            int4(from7p(hi[1].r, int(pbits.w)), from7p(hi[1].g, int(pbits.w)), from7p(hi[1].b, int(pbits.w)), 0), 3, false);
        uint2 weights = evalWeights(px, line0, line1, line0, map, 3);

        if (pass40 == 1)
            return makeCandidate(encodeMode3RgbBlock(partId, lo, hi, pbits, weights, map), totalMode3Sse);

        [unroll]
        for (uint s41 = 0; s41 < 2; ++s41)
        {
            float4 xl, xh;
            if (!leastSquaresSubset(px, weights, 3u, map, s41, float4(subsetTotals[s41].rgb, 0), xl, xh))
                continue;

            if (pbitOpt)
            {
                int4 minColor, maxColor;
                uint2 pair;
                determinePBits(3, 7, float4(clamp(xl.rgb * (1.f / 255.f), 0.f, 1.f), 0),
                    float4(clamp(xh.rgb * (1.f / 255.f), 0.f, 1.f), 0), false, minColor, maxColor, pair);
                pbits[s41 * 2] = pair.x;
                pbits[s41 * 2 + 1] = pair.y;
                lo[s41] = minColor.rgb;
                hi[s41] = maxColor.rgb;
            }
            else
            {
                int lowPBit = xl.r + xl.g + xl.b >= 129.f * 3.f ? 1 : 0;
                int highPBit = xh.r + xh.g + xh.b >= 129.f * 3.f ? 1 : 0;
                pbits[s41 * 2] = uint(lowPBit);
                pbits[s41 * 2 + 1] = uint(highPBit);
                lo[s41] = int3(to7pClamp(xl.r, lowPBit), to7pClamp(xl.g, lowPBit), to7pClamp(xl.b, lowPBit));
                hi[s41] = int3(to7pClamp(xh.r, highPBit), to7pClamp(xh.g, highPBit), to7pClamp(xh.b, highPBit));
            }
        }
    }
    return result;
}

Candidate packMode7Rgba(uint px[16], float4 blockAxis, int4 blockMean, float sseEstimateToBeat, uint flags)
{
    Candidate result;
    result.ok = false;
    result.block = uint4(0, 0, 0, 0);
    result.sse = NoCandidate;

    uint partId = bestPattern2(splitAlongAxis(px, blockAxis, blockMean, true));
    uint map = part2Map(partId);

    int4 subsetTotals[2];
    int subsetCount[2];
    int4 subsetLow[2];
    int4 subsetHigh[2];
    float slamToLineSse = 0.f;

    [unroll]
    for (uint s42 = 0; s42 < 2; ++s42)
    {
        subsetTotals[s42] = subsetSums(px, map, s42, subsetCount[s42]);
        int t = subsetCount[s42];
        int4 mean = (subsetTotals[s42] + (t >> 1)) / t;

        int icov[10];
        covariance4(px, mean, map, s42, icov);
        int blockMaxVar = max(max(icov[0], icov[4]), max(icov[7], icov[9]));
        float sc = blockMaxVar != 0 ? 1.f / blockMaxVar : 0.f;
        float4 axis = iterateAxis4(icov, sc * float4(icov[0], icov[4], icov[7], icov[9]));
        axis = iterateAxis4(icov, axis);

        float ignored;
        slamToLineSse += estimateSlamToLineSse4D(icov, axis, ignored);

        uint lowTexel, highTexel;
        findExtremes(px, scaledAxis4(axis), true, map, s42, lowTexel, highTexel);
        subsetLow[s42] = unpack(lowTexel);
        subsetHigh[s42] = unpack(highTexel);
    }

    bool pbitOpt = (flags & FLAG_PBIT_OPT) != 0u;
    float quantSse = 0.f;
    [unroll]
    for (uint s43 = 0; s43 < 2; ++s43)
        quantSse += analyticalQuantEstimateSse(32, 4, 4, subsetHigh[s43] - subsetLow[s43],
            pbitOpt ? UniquePBitDiscount : 1.f, subsetCount[s43]);

    float totalSse = slamToLineSse + quantSse;
    result.sse = totalSse;
    if (totalSse >= sseEstimateToBeat)
        return result;

    int4 lo[2];
    int4 hi[2];
    uint4 pbits = uint4(0, 0, 0, 0);

    [unroll]
    for (uint s44 = 0; s44 < 2; ++s44)
    {
        if (pbitOpt)
        {
            int4 minColor, maxColor;
            uint2 pair;
            determinePBits(4, 5, float4(subsetLow[s44]) * (1.f / 255.f), float4(subsetHigh[s44]) * (1.f / 255.f),
                false, minColor, maxColor, pair);
            pbits[s44 * 2] = pair.x;
            pbits[s44 * 2 + 1] = pair.y;
            lo[s44] = minColor;
            hi[s44] = maxColor;
        }
        else
        {
            int lowPBit = subsetLow[s44].a >= 129 ? 1 : 0;
            int highPBit = subsetHigh[s44].a >= 129 ? 1 : 0;
            pbits[s44 * 2] = uint(lowPBit);
            pbits[s44 * 2 + 1] = uint(highPBit);
            lo[s44] = int4(to5p(subsetLow[s44].r, lowPBit), to5p(subsetLow[s44].g, lowPBit),
                to5p(subsetLow[s44].b, lowPBit), to5p(subsetLow[s44].a, lowPBit));
            hi[s44] = int4(to5p(subsetHigh[s44].r, highPBit), to5p(subsetHigh[s44].g, highPBit),
                to5p(subsetHigh[s44].b, highPBit), to5p(subsetHigh[s44].a, highPBit));
        }
    }

    [unroll]
    for (uint pass45 = 0; pass45 < 2; ++pass45)
    {
        SubsetLine line0 = makeLine(
            int4(from5p(lo[0].r, int(pbits.x)), from5p(lo[0].g, int(pbits.x)), from5p(lo[0].b, int(pbits.x)), from5p(lo[0].a, int(pbits.x))),
            int4(from5p(hi[0].r, int(pbits.y)), from5p(hi[0].g, int(pbits.y)), from5p(hi[0].b, int(pbits.y)), from5p(hi[0].a, int(pbits.y))), 3, true);
        SubsetLine line1 = makeLine(
            int4(from5p(lo[1].r, int(pbits.z)), from5p(lo[1].g, int(pbits.z)), from5p(lo[1].b, int(pbits.z)), from5p(lo[1].a, int(pbits.z))),
            int4(from5p(hi[1].r, int(pbits.w)), from5p(hi[1].g, int(pbits.w)), from5p(hi[1].b, int(pbits.w)), from5p(hi[1].a, int(pbits.w))), 3, true);
        uint2 weights = evalWeights(px, line0, line1, line0, map, 3);

        if (pass45 == 1)
            return makeCandidate(encodeMode7RgbaBlock(partId, lo, hi, pbits, weights, map), totalSse);

        [unroll]
        for (uint s46 = 0; s46 < 2; ++s46)
        {
            float4 xl, xh;
            if (!leastSquaresSubset(px, weights, 3u, map, s46, float4(subsetTotals[s46]), xl, xh))
                continue;

            if (pbitOpt)
            {
                int4 minColor, maxColor;
                uint2 pair;
                determinePBits(4, 5, clamp(xl * (1.f / 255.f), 0.f, 1.f), clamp(xh * (1.f / 255.f), 0.f, 1.f),
                    false, minColor, maxColor, pair);
                pbits[s46 * 2] = pair.x;
                pbits[s46 * 2 + 1] = pair.y;
                lo[s46] = minColor;
                hi[s46] = maxColor;
            }
            else
            {
                int lowPBit = xl.a >= 129.f ? 1 : 0;
                int highPBit = xh.a >= 129.f ? 1 : 0;
                pbits[s46 * 2] = uint(lowPBit);
                pbits[s46 * 2 + 1] = uint(highPBit);
                lo[s46] = int4(to5pClamp(xl.r, lowPBit), to5pClamp(xl.g, lowPBit), to5pClamp(xl.b, lowPBit), to5pClamp(xl.a, lowPBit));
                hi[s46] = int4(to5pClamp(xh.r, highPBit), to5pClamp(xh.g, highPBit), to5pClamp(xh.b, highPBit), to5pClamp(xh.a, highPBit));
            }
        }
    }
    return result;
}

int distance3(int3 a, int3 b) { int3 d = a - b; return d.r * d.r + d.g * d.g + d.b * d.b; }

bool determineThreeSubsets(uint px[16], float3 blockAxis, int3 blockMean, out uint desiredMap)
{
    desiredMap = 0;

    uint split = splitAlongAxis(px, float4(blockAxis, 0), int4(blockMean, 0), false);

    int3 subsetSum[2];
    int subsetTotal[2];
    [unroll]
    for (uint s47 = 0; s47 < 2; ++s47)
    {
        subsetSum[s47] = int3(0, 0, 0);
        subsetTotal[s47] = 0;
        [unroll]
        for (uint i48 = 0; i48 < 16; ++i48)
            if (((split >> i48) & 1u) == s47)
            {
                subsetSum[s47] += unpack(px[i48]).rgb;
                subsetTotal[s47]++;
            }

        if (subsetTotal[s47] == 0)
            return false;
        subsetSum[s47] = (subsetSum[s47] + (subsetTotal[s47] >> 1)) / subsetTotal[s47];
    }

    int subsetSse[2];
    [unroll]
    for (uint s49 = 0; s49 < 2; ++s49)
    {
        subsetSse[s49] = 0;
        [unroll]
        for (uint i50 = 0; i50 < 16; ++i50)
            if (((split >> i50) & 1u) == s49)
                subsetSse[s49] += distance3(unpack(px[i50]).rgb, subsetSum[s49]);
    }

    uint subsetToSplit = subsetSse[1] > subsetSse[0] ? 1u : 0u;
    if (subsetTotal[subsetToSplit] < 2)
        return false;

    int lowY = 0x7fffffff;
    int highY = 0;
    uint lowTexel = px[0], highTexel = px[0];
    [unroll]
    for (uint i51 = 0; i51 < 16; ++i51)
    {
        if (((split >> i51) & 1u) != subsetToSplit)
            continue;
        int3 t = unpack(px[i51]).rgb;
        int y = ((t.r + t.g + t.b) << 4) + int(i51);
        if (y < lowY) { lowY = y; lowTexel = px[i51]; }
        if (y > highY) { highY = y; highTexel = px[i51]; }
    }

    if ((lowY & 15) == (highY & 15))
        return false;

    int3 low = unpack(lowTexel).rgb;
    int3 high = unpack(highTexel).rgb;

    [unroll]
    for (uint i52 = 0; i52 < 16; ++i52)
    {
        uint subset = 2u;
        if (((split >> i52) & 1u) == subsetToSplit)
        {
            int3 t = unpack(px[i52]).rgb;
            subset = distance3(high, t) > distance3(low, t) ? 1u : 0u;
        }
        desiredMap |= subset << (i52 * 2u);
    }
    return true;
}

Candidate packMode0Or2Rgb(uint px[16], float3 blockAxis, int3 blockMean, float sseEstimateToBeat)
{
    Candidate result;
    result.ok = false;
    result.block = uint4(0, 0, 0, 0);
    result.sse = NoCandidate;

    uint desiredMap;
    if (!determineThreeSubsets(px, blockAxis, blockMean, desiredMap))
        return result;

    uint patId[2];
    patId[1] = bestPattern3(desiredMap, patId[0]);

    int4 modeTotals[2][3];
    int modeCount[2][3];
    int4 modeLow[2][3];
    int4 modeHigh[2][3];
    float modeSlamSse[2];
    float modeQuantSse[2];

    [unroll]
    for (uint modeIter = 0; modeIter < 2; ++modeIter)
    {
        if (modeIter != 0u && patId[0] == patId[1])
        {
            [unroll]
            for (uint s53 = 0; s53 < 3; ++s53)
            {
                modeTotals[1][s53] = modeTotals[0][s53];
                modeCount[1][s53] = modeCount[0][s53];
                modeLow[1][s53] = modeLow[0][s53];
                modeHigh[1][s53] = modeHigh[0][s53];
            }
            modeSlamSse[1] = modeSlamSse[0];
        }
        else
        {
            uint map = part3Map(patId[modeIter]);
            float slamSse = 0.f;
            [unroll]
            for (uint s54 = 0; s54 < 3; ++s54)
            {
                modeTotals[modeIter][s54] = subsetSums(px, map, s54, modeCount[modeIter][s54]);
                int t = modeCount[modeIter][s54];
                int3 mean = (modeTotals[modeIter][s54].rgb + (t >> 1)) / t;

                int icov[6];
                covariance3(px, mean, map, s54, icov);
                int blockMaxVar = max(max(icov[0], icov[3]), icov[5]);
                float3 altX = principalAxis3(icov, 1.f / (blockMaxVar + .0000125f));

                float ignored;
                slamSse += estimateSlamToLineSse3D(icov, altX, ignored);

                uint lowTexel, highTexel;
                findExtremes(px, int4(scaledAxis3(altX), 0), false, map, s54, lowTexel, highTexel);
                modeLow[modeIter][s54] = unpack(lowTexel);
                modeHigh[modeIter][s54] = unpack(highTexel);
            }
            modeSlamSse[modeIter] = slamSse;
        }

        float quantSse = 0.f;
        [unroll]
        for (uint s55 = 0; s55 < 3; ++s55)
        {
            int4 spans = int4(modeHigh[modeIter][s55].rgb - modeLow[modeIter][s55].rgb, 0);
            quantSse += modeIter == 0u
                ? analyticalQuantEstimateSse(16, 8, 3, spans, UniquePBitDiscount, modeCount[modeIter][s55])
                : analyticalQuantEstimateSse(32, 4, 3, spans, 1.f, modeCount[modeIter][s55]);
        }
        modeQuantSse[modeIter] = quantSse;
    }

    float mode0Sse = modeQuantSse[0] + modeSlamSse[0];
    float mode2Sse = modeQuantSse[1] + modeSlamSse[1];

    int3 lo[3];
    int3 hi[3];

    if (mode0Sse < mode2Sse)
    {
        result.sse = mode0Sse;
        if (mode0Sse >= sseEstimateToBeat)
            return result;

        uint partId = patId[0];
        uint map = part3Map(partId);
        uint pbits[6];
        float4 wantedLow[3];
        float4 wantedHigh[3];

        [unroll]
        for (uint s56 = 0; s56 < 3; ++s56)
        {
            wantedLow[s56] = float4(modeLow[0][s56].rgb, 0) * (1.f / 255.f);
            wantedHigh[s56] = float4(modeHigh[0][s56].rgb, 0) * (1.f / 255.f);
        }

        [unroll]
        for (uint pass57 = 0; pass57 < 2; ++pass57)
        {
            [unroll]
            for (uint s58 = 0; s58 < 3; ++s58)
            {
                int4 minColor, maxColor;
                uint2 pair;
                determinePBits(3, 4, wantedLow[s58], wantedHigh[s58], false, minColor, maxColor, pair);
                pbits[s58 * 2] = pair.x;
                pbits[s58 * 2 + 1] = pair.y;
                lo[s58] = minColor.rgb;
                hi[s58] = maxColor.rgb;
            }

            SubsetLine edge[3];
            [unroll]
            for (uint s59 = 0; s59 < 3; ++s59)
            {
                int p0 = int(pbits[s59 * 2]);
                int p1 = int(pbits[s59 * 2 + 1]);
                edge[s59] = makeLine(
                    int4(from4(lo[s59].r, p0), from4(lo[s59].g, p0), from4(lo[s59].b, p0), 0),
                    int4(from4(hi[s59].r, p1), from4(hi[s59].g, p1), from4(hi[s59].b, p1), 0), 7, false);
            }
            uint2 weights = evalWeights(px, edge[0], edge[1], edge[2], map, 7);

            if (pass57 == 1)
                return makeCandidate(encodeMode0RgbBlock(partId, lo, hi, pbits, weights, map), mode0Sse);

            [unroll]
            for (uint s60 = 0; s60 < 3; ++s60)
            {
                float4 xl, xh;
                if (!leastSquaresSubset(px, weights, 7u, map, s60, float4(modeTotals[0][s60].rgb, 0), xl, xh))
                    continue;
                wantedLow[s60] = float4(clamp(xl.rgb * (1.f / 255.f), 0.f, 1.f), 0);
                wantedHigh[s60] = float4(clamp(xh.rgb * (1.f / 255.f), 0.f, 1.f), 0);
            }
        }
        return result;
    }

    result.sse = mode2Sse;
    if (mode2Sse >= sseEstimateToBeat)
        return result;

    uint partId = patId[1];
    uint map = part3Map(partId);

    [unroll]
    for (uint s61 = 0; s61 < 3; ++s61)
    {
        lo[s61] = int3(to5(modeLow[1][s61].r), to5(modeLow[1][s61].g), to5(modeLow[1][s61].b));
        hi[s61] = int3(to5(modeHigh[1][s61].r), to5(modeHigh[1][s61].g), to5(modeHigh[1][s61].b));
    }

    [unroll]
    for (uint pass62 = 0; pass62 < 2; ++pass62)
    {
        SubsetLine edge[3];
        [unroll]
        for (uint s63 = 0; s63 < 3; ++s63)
            edge[s63] = makeLine(
                int4(from5(lo[s63].r), from5(lo[s63].g), from5(lo[s63].b), 0),
                int4(from5(hi[s63].r), from5(hi[s63].g), from5(hi[s63].b), 0), 3, false);
        uint2 weights = evalWeights(px, edge[0], edge[1], edge[2], map, 3);

        if (pass62 == 1)
            return makeCandidate(encodeMode2RgbBlock(partId, lo, hi, weights, map), mode2Sse);

        [unroll]
        for (uint s64 = 0; s64 < 3; ++s64)
        {
            float4 xl, xh;
            if (!leastSquaresSubset(px, weights, 3u, map, s64, float4(modeTotals[1][s64].rgb, 0), xl, xh))
                continue;
            lo[s64] = int3(to5Clamp(xl.r), to5Clamp(xl.g), to5Clamp(xl.b));
            hi[s64] = int3(to5Clamp(xh.r), to5Clamp(xh.g), to5Clamp(xh.b));
        }
    }
    return result;
}

bool isSolid(uint px[16])
{
    uint same = 0;
    [unroll]
    for (uint i65 = 1; i65 < 16; ++i65)
        same |= px[i65] ^ px[0];
    return same == 0u;
}

// Squared correlation kept as numerator and denominator so comparisons cross-multiply instead of dividing or
// taking sqrt: Vulkan lets OpFDiv and sqrt be several ULP off, which would break agreement with
// Bc7BlockEncoder and across GPU vendors.
struct SquaredCorrelation
{
    float numerator;
    float denominator;
};

// Inactive is the reference's flat-channel fallback, a correlation of one; folded in here because dxc's
// conditional operator rejects struct results.
SquaredCorrelation correlationOf(bool active, int cov, float varianceA, float varianceB)
{
    SquaredCorrelation c;
    c.numerator = 1.f;
    c.denominator = 1.f;
    if (active)
    {
        c.numerator = float(cov) * float(cov);
        c.denominator = varianceA * varianceB;
    }
    return c;
}

bool correlationLess(SquaredCorrelation a, SquaredCorrelation b)
{
    return a.numerator * b.denominator < b.numerator * a.denominator;
}

bool correlationAtLeast(SquaredCorrelation c, float threshold)
{
    return c.numerator >= threshold * threshold * c.denominator;
}

int pickDualPlaneChannelRgb(int icov[6])
{
    bool hasR = icov[0] > 16;
    bool hasG = icov[3] > 16;
    bool hasB = icov[5] > 16;
    int active = (hasR ? 1 : 0) + (hasG ? 1 : 0) + (hasB ? 1 : 0);
    if (active < 2)
        return -1;

    float vr = icov[0], vg = icov[3], vb = icov[5];
    SquaredCorrelation rg = correlationOf(hasR && hasG, icov[1], vr, vg);
    SquaredCorrelation rb = correlationOf(hasR && hasB, icov[2], vr, vb);
    SquaredCorrelation gb = correlationOf(hasG && hasB, icov[4], vg, vb);

    if (correlationAtLeast(rg, StrongCorrThresh) && correlationAtLeast(rb, StrongCorrThresh)
        && correlationAtLeast(gb, StrongCorrThresh))
        return -1;
    if (active == 2)
        return !hasR ? 1 : 0;
    if (correlationLess(rg, gb) && correlationLess(rb, gb))
        return 0;
    if (correlationLess(rg, rb) && correlationLess(gb, rb))
        return 1;
    return 2;
}

int pickDualPlaneChannelRgba(int icov[10])
{
    float vr = icov[0], vg = icov[4], vb = icov[7], va = icov[9];

    if (icov[9] > 0)
    {
        SquaredCorrelation ra = correlationOf(icov[0] != 0, icov[3], vr, va);
        SquaredCorrelation ga = correlationOf(icov[4] != 0, icov[6], vg, va);
        SquaredCorrelation ba = correlationOf(icov[7] != 0, icov[8], vb, va);
        if (!correlationAtLeast(ra, AlphaDecorrThreshold) || !correlationAtLeast(ga, AlphaDecorrThreshold)
            || !correlationAtLeast(ba, AlphaDecorrThreshold))
            return 3;
    }

    bool hasR = icov[0] > 16;
    bool hasG = icov[4] > 16;
    bool hasB = icov[7] > 16;
    int active = (hasR ? 1 : 0) + (hasG ? 1 : 0) + (hasB ? 1 : 0);
    if (active < 2)
        return -1;

    SquaredCorrelation rg = correlationOf(hasR && hasG, icov[1], vr, vg);
    SquaredCorrelation rb = correlationOf(hasR && hasB, icov[2], vr, vb);
    SquaredCorrelation gb = correlationOf(hasG && hasB, icov[5], vg, vb);

    if (correlationAtLeast(rg, StrongDecorrThreshRgba) && correlationAtLeast(rb, StrongDecorrThreshRgba)
        && correlationAtLeast(gb, StrongDecorrThreshRgba))
        return -1;
    if (active == 2)
        return !hasR ? 1 : 0;
    if (correlationLess(rg, gb) && correlationLess(rb, gb))
        return 0;
    if (correlationLess(rg, rb) && correlationLess(gb, rb))
        return 1;
    return 2;
}

uint4 packMode6Rgb(uint px[16], int3 lo8, int3 hi8, int3 totals, bool refine, uint flags)
{
    bool pbitOpt = (flags & FLAG_PBIT_OPT_MODE6) != 0u;
    int p0 = 1, p1 = 1;
    int3 lo, hi;

    if (pbitOpt)
    {
        int4 minColor, maxColor;
        uint2 pair;
        determinePBits(3, 7, float4(lo8, 0) * (1.f / 255.f), float4(hi8, 0) * (1.f / 255.f),
            false, minColor, maxColor, pair);
        p0 = int(pair.x);
        p1 = int(pair.y);
        lo = minColor.rgb;
        hi = maxColor.rgb;
    }
    else
    {
        // Both p-bits forced to 1 keeps alpha at 255 without costing what the optimiser does.
        lo = int3(to7p(lo8.r, p0), to7p(lo8.g, p0), to7p(lo8.b, p0));
        hi = int3(to7p(hi8.r, p1), to7p(hi8.g, p1), to7p(hi8.b, p1));
    }

    uint2 weights = evalWeightsMode6Rgb(px, lo, hi, p0, p1);

    if (refine)
    {
        float4 xl, xh;
        if (leastSquares(px, weights, 15u, float4(totals, 0), false, xl, xh))
        {
            if (pbitOpt)
            {
                int4 minColor, maxColor;
                uint2 pair;
                determinePBits(3, 7, float4(xl.rgb, 0) * (1.f / 255.f), float4(xh.rgb, 0) * (1.f / 255.f),
                    false, minColor, maxColor, pair);
                p0 = int(pair.x);
                p1 = int(pair.y);
                lo = minColor.rgb;
                hi = maxColor.rgb;
            }
            else
            {
                p0 = 1;
                p1 = 1;
                lo = int3(to7pf(xl.r, p0), to7pf(xl.g, p0), to7pf(xl.b, p0));
                hi = int3(to7pf(xh.r, p1), to7pf(xh.g, p1), to7pf(xh.b, p1));
            }
            weights = evalWeightsMode6Rgb(px, lo, hi, p0, p1);
        }
    }

    return encodeMode6RgbaBlock(int4(lo, 127), p0, int4(hi, 127), p1, weights);
}

uint4 packRgb(uint px[16], uint flags)
{
    if (isSolid(px))
        return packMode5Solid(px[0]);

    int4 sums, mins, maxes;
    channelStats(px, sums, mins, maxes);
    int3 mean = (sums.rgb + 8) >> 4;

    int icov[6];
    covariance3(px, mean, 0u, 0u, icov);
    int blockMaxVar = max(max(icov[0], icov[3]), icov[5]);

    // Not redundant with isSolid: that compares whole RGBA words, so alpha can hide a constant RGB block.
    if (blockMaxVar == 0)
        return packMode5Solid(px[0]);

    int dpChannel = -1;
    if ((flags & FLAG_USE_DUAL_PLANE_RGB) != 0u && blockMaxVar >= DpBlockVarThresh)
        dpChannel = pickDualPlaneChannelRgb(icov);

    if ((flags & FLAG_USE_TRIVIAL_MODE6) != 0u && dpChannel == -1 && blockMaxVar < TrivialBlockThreshRgb)
    {
        uint lowTexel, highTexel;
        findExtremes(px, int4(16 * 2, 16 * 4, 16, 0), false, 0u, 0u, lowTexel, highTexel);
        return packMode6Rgb(px, unpack(lowTexel).rgb, unpack(highTexel).rgb, sums.rgb, false, flags);
    }

    float3 altX = principalAxis3(icov, 1.f / blockMaxVar);
    int4 spans = int4(maxes.rgb - mins.rgb, 0);

    // Must match the branches below exactly: a wrong answer here changes mode selection, not just speed.
    bool needSseEstimates = (flags & FLAG_USE_2_SUBSETS_RGB) != 0u || dpChannel >= 0;

    float mode6OrthoRatio = 0.f;
    float mode6SlamToLineSse = needSseEstimates ? estimateSlamToLineSse3D(icov, altX, mode6OrthoRatio) : 0.f;
    float mode6Sse = needSseEstimates ? mode6SlamToLineSse + analyticalQuantEstimateSse(128, 16, 3, spans, 1.f, 16) : 0.f;

    if ((flags & FLAG_USE_2_SUBSETS_RGB) != 0u && blockMaxVar >= MinBlockMaxVar23Subsets
        && mode6OrthoRatio > OrthoRatio23SubsetRatioThresh && mode6SlamToLineSse >= HighOrthoEnergyThresh)
    {
        if ((flags & FLAG_USE_3_SUBSETS_RGB) != 0u && blockMaxVar >= MinBlockMaxVar3Subsets)
        {
            Candidate threeSubset = packMode0Or2Rgb(px, altX, mean, mode6Sse);
            if (threeSubset.ok)
            {
                Candidate twoSubset = packMode1Or3Rgb(px, altX, mean, threeSubset.sse, flags);
                return twoSubset.ok ? twoSubset.block : threeSubset.block;
            }
        }

        Candidate twoSubset = packMode1Or3Rgb(px, altX, mean, mode6Sse, flags);
        if (twoSubset.ok)
            return twoSubset.block;
    }

    if (dpChannel >= 0)
    {
        Candidate dual = packMode4Or5(px, uint(dpChannel), mode6Sse);
        if (dual.ok)
            return dual.block;
    }

    uint lowTexel, highTexel;
    findExtremes(px, int4(scaledAxis3(altX), 0), false, 0u, 0u, lowTexel, highTexel);
    return packMode6Rgb(px, unpack(lowTexel).rgb, unpack(highTexel).rgb, sums.rgb, true, flags);
}

uint4 packMode6Rgba(uint px[16], int4 lo8, int4 hi8, int4 totals, bool refine, uint flags)
{
    bool pbitOpt = (flags & FLAG_PBIT_OPT_MODE6) != 0u;
    int p0, p1;
    int4 lo, hi;

    if (pbitOpt)
    {
        int4 minColor, maxColor;
        uint2 pair;
        determinePBits(4, 7, float4(lo8) * (1.f / 255.f), float4(hi8) * (1.f / 255.f),
            false, minColor, maxColor, pair);
        p0 = int(pair.x);
        p1 = int(pair.y);
        lo = minColor;
        hi = maxColor;
    }
    else
    {
        p0 = lo8.a > 128 ? 1 : 0;
        p1 = hi8.a > 128 ? 1 : 0;
        lo = int4(to7p(lo8.r, p0), to7p(lo8.g, p0), to7p(lo8.b, p0), to7p(lo8.a, p0));
        hi = int4(to7p(hi8.r, p1), to7p(hi8.g, p1), to7p(hi8.b, p1), to7p(hi8.a, p1));
    }

    uint2 weights = evalWeightsMode6Rgba(px, lo, hi, p0, p1);

    if (refine)
    {
        float4 xl, xh;
        if (leastSquares(px, weights, 15u, float4(totals), true, xl, xh))
        {
            if (pbitOpt)
            {
                int4 minColor, maxColor;
                uint2 pair;
                determinePBits(4, 7, xl * (1.f / 255.f), xh * (1.f / 255.f), false, minColor, maxColor, pair);
                p0 = int(pair.x);
                p1 = int(pair.y);
                lo = minColor;
                hi = maxColor;
            }
            else
            {
                p0 = xl.a >= 129.f ? 1 : 0;
                p1 = xh.a >= 129.f ? 1 : 0;
                lo = int4(to7pf(xl.r, p0), to7pf(xl.g, p0), to7pf(xl.b, p0), to7pf(xl.a, p0));
                hi = int4(to7pf(xh.r, p1), to7pf(xh.g, p1), to7pf(xh.b, p1), to7pf(xh.a, p1));
            }
            weights = evalWeightsMode6Rgba(px, lo, hi, p0, p1);
        }
    }

    return encodeMode6RgbaBlock(lo, p0, hi, p1, weights);
}

uint4 packRgba(uint px[16], uint flags)
{
    if (isSolid(px))
        return packMode5Solid(px[0]);

    int4 sums, mins, maxes;
    channelStats(px, sums, mins, maxes);
    int4 mean = (sums + 8) >> 4;

    int icov[10];
    covariance4(px, mean, 0u, 0u, icov);
    int blockMaxVar = max(max(icov[0], icov[4]), max(icov[7], icov[9]));

    int dpChannel = -1;
    if ((flags & FLAG_USE_DUAL_PLANE_RGBA) != 0u && blockMaxVar >= DpBlockVarThreshRgba)
        dpChannel = pickDualPlaneChannelRgba(icov);

    if ((flags & FLAG_USE_TRIVIAL_MODE6) != 0u && dpChannel == -1 && blockMaxVar < TrivialBlockThreshRgba)
    {
        uint lowTexel, highTexel;
        findExtremes(px, int4(16 * 2, 16 * 4, 16, 16 * 4), true, 0u, 0u, lowTexel, highTexel);
        return packMode6Rgba(px, unpack(lowTexel), unpack(highTexel), sums, false, flags);
    }

    float sc = blockMaxVar != 0 ? 1.f / blockMaxVar : 0.f;
    float4 w = sc * float4(icov[0], icov[4], icov[7], icov[9]);

    float4 x1 = float4(0, 0, 0, 0);
    [unroll]
    for (uint it66 = 0; it66 < 4; ++it66)
    {
        x1 = iterateAxis4(icov, w);
        float t = sqrt(dot(x1, x1));
        x1 = t > SmallFloatValue ? x1 * (1.f / t) : float4(.25f, .25f, .25f, .25f);
        w = x1;
    }

    bool needSseEstimates = (flags & (FLAG_USE_2_SUBSETS_RGBA | FLAG_USE_DUAL_PLANE_RGBA)) != 0u;
    float mode6OrthoRatio = 0.f;
    float mode6SlamToLineSse = needSseEstimates ? estimateSlamToLineSse4D(icov, x1, mode6OrthoRatio) : 0.f;
    float mode6Sse = needSseEstimates
        ? mode6SlamToLineSse + analyticalQuantEstimateSse(128, 16, 4, int4(maxes - mins), 1.f, 16)
        : 0.f;

    Candidate dual;
    dual.ok = false;
    dual.block = uint4(0, 0, 0, 0);
    dual.sse = NoCandidate;
    if (dpChannel >= 0)
        dual = packMode4Or5(px, uint(dpChannel), mode6Sse);

    // Mode 7 must beat mode 4/5 as well as mode 6, so the lower of the two lets it bail before encoding a loser.
    Candidate twoSubset;
    twoSubset.ok = false;
    twoSubset.block = uint4(0, 0, 0, 0);
    twoSubset.sse = NoCandidate;
    if ((flags & FLAG_USE_2_SUBSETS_RGBA) != 0u && blockMaxVar >= MinBlockMaxVar23SubsetsRgba
        && mode6OrthoRatio > OrthoRatio23SubsetRatioThreshRgba && mode6SlamToLineSse >= HighOrthoEnergyThreshRgba)
        twoSubset = packMode7Rgba(px, x1, mean, min(mode6Sse, dual.sse), flags);

    if (dual.sse < twoSubset.sse && dual.sse < mode6Sse)
        return dual.block;
    if (twoSubset.sse < dual.sse && twoSubset.sse < mode6Sse)
        return twoSubset.block;

    uint lowTexel, highTexel;
    findExtremes(px, scaledAxis4(x1), true, 0u, 0u, lowTexel, highTexel);
    return packMode6Rgba(px, unpack(lowTexel), unpack(highTexel), sums, true, flags);
}

// Bc7PackFlags.WaylithDefault (bc7f's default without PBitOptMode6, which leaves opaque blocks at alpha 254)
// minus the 2/3-subset modes: under 1% of blocks take them, yet a wave runs the search if any lane does (2x
// encode on M1), and on a mixed corpus they lost more PSNR than they won.
static const uint GpuFlags = FLAG_USE_DUAL_PLANE_RGB | FLAG_USE_DUAL_PLANE_RGBA | FLAG_PBIT_OPT
    | FLAG_USE_TRIVIAL_MODE6;

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= pc.numBlocksX || tid.y >= pc.numBlocksY)
        return;

    uint px[16];
    loadBlockPacked(tid.x, tid.y, px);

    uint allAlpha = px[0];
    [unroll]
    for (uint i67 = 1; i67 < 16; ++i67)
        allAlpha &= px[i67];

    uint4 block = (allAlpha >> 24u) == 255u ? packRgb(px, GpuFlags) : packRgba(px, GpuFlags);
    _Blocks.Store4(blockByteOffset(tid.xy, 16u), block);
}
