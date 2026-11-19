// Derived from basis_universal (https://github.com/BinomialLLC/basis_universal), Copyright (C) 2016-2026 Binomial LLC.
// Licensed under the Apache License, Version 2.0. Modified from the original.

// A port of Bc1BlockEncoder (basis_universal's SPMD bc1 encoder), lane for thread.
//
// Rounding is half-to-even, as Vector<float> does it, so the output stays within a few blocks in 10^4 of the
// CPU encoder. The rest of the gap is fma contraction and approximate division; marking the float steps precise
// closed only part of it and made the kernel twice as slow on Apple GPUs.
//
// Loop counters are numbered because slang's HLSL mode hoists a for-loop declaration to function scope.

#include "BCEncodeCommon.hlsl"
#include "BCn/bc1_tables.h"

static const int LeastSquaresRounds = 2;
static const int AverageVarianceThreshold = 16;

// Spelled out: an int dot() emits SPV_KHR_integer_dot_product, which not every driver takes.
int idot(int3 a, int3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

// Texels stay packed and are unpacked at each use: 16 uints instead of 48 ints, which on Apple GPUs is the
// difference between registers and stack.
int3 rgbOf(uint texel) { return int3(texel & 255u, (texel >> 8) & 255u, (texel >> 16) & 255u); }

int div3(int x) { return int((uint(x) * 43691u) >> 17); }
int3 div3(int3 x) { return int3((uint3(x) * 43691u) >> 17); }

int3 expand565(int c)
{
    int3 bits = int3((c >> 11) & 31, (c >> 5) & 63, c & 31);
    return int3((bits.x << 3) | (bits.x >> 2), (bits.y << 2) | (bits.y >> 4), (bits.z << 3) | (bits.z >> 2));
}

float roundHalfEven(float x)
{
    float f = floor(x);
    float d = x - f;
    return (d > 0.5f || (d == 0.5f && fmod(f, 2.0f) != 0.0f)) ? f + 1.0f : f;
}

int round565(float value, float scale)
{
    float scaled = clamp(value, 0.0f, 255.0f) * scale;
    return int(roundHalfEven(scaled));
}

int to565(float3 c)
{
    return (round565(c.x, 31.0f / 255.0f) << 11) | (round565(c.y, 63.0f / 255.0f) << 5) | round565(c.z, 31.0f / 255.0f);
}

struct Palette
{
    int3 e0, e1, e2, e3;
    int t0, t1, t2;
    int3 axis2;
};

Palette makePalette(int c0, int c1)
{
    Palette p;
    p.e0 = expand565(c0);
    p.e1 = expand565(c1);
    p.e2 = div3(2 * p.e0 + p.e1);
    p.e3 = div3(p.e0 + 2 * p.e1);

    // The asymmetric first compare biases extreme texels onto the outer selectors.
    int3 axis = p.e1 - p.e0;
    int dp0 = idot(p.e0, axis);
    int dp1 = idot(p.e1, axis);
    int dp2 = idot(p.e2, axis);
    int dp3 = idot(p.e3, axis);
    p.t0 = dp0 + dp2;
    p.t1 = dp2 + dp3;
    p.t2 = dp3 + dp1;
    p.axis2 = axis + axis;
    return p;
}

int selectorOf(Palette p, int3 c)
{
    int d = idot(c, p.axis2);
    if (d <= p.t0)
        return 0;
    if (d < p.t1)
        return 2;
    if (d < p.t2)
        return 3;
    return 1;
}

void solidEndpoints(int3 sum, out int high, out int low)
{
    int3 index = (sum + 8) >> 4;
    uint red = Bc1Omatch5[index.x];
    uint green = Bc1Omatch6[index.y];
    uint blue = Bc1Omatch5[index.z];
    high = int(((red >> 16) << 11) | ((green >> 16) << 5) | (blue >> 16));
    low = int(((red & 0xFFFFu) << 11) | ((green & 0xFFFFu) << 5) | (blue & 0xFFFFu));
}

void separate(inout int c0, inout int c1)
{
    int high = max(c0, c1);
    int low = min(c0, c1);
    bool equal = high == low;
    bool positive = low > 0;
    c0 = (equal && !positive) ? high + 1 : high;
    c1 = (equal && positive) ? low - 1 : low;
}

void leastSquaresRound(uint px[16], inout int c0, inout int c1)
{
    Palette p = makePalette(c0, c1);
    float m00 = 0.0f;
    float m01 = 0.0f;
    float m11 = 0.0f;
    float3 a0 = 0.0f;
    float3 a1 = 0.0f;

    [unroll]
    for (int i1 = 0; i1 < 16; ++i1)
    {
        int3 c = rgbOf(px[i1]);
        int selector = selectorOf(p, c);
        float tb = selector == 0 ? 0.0f : selector == 1 ? 1.0f : selector == 2 ? 1.0f / 3.0f : 2.0f / 3.0f;
        float ta = 1.0f - tb;
        m00 += ta * ta;
        m01 += ta * tb;
        m11 += tb * tb;
        float3 cf = float3(c);
        a0 += ta * cf;
        a1 += tb * cf;
    }

    float determinant = m00 * m11 - m01 * m01;
    float inverse = 1.0f / max(determinant, 1e-6f);
    float3 endpoint0 = (a0 * m11 - a1 * m01) * inverse;
    float3 endpoint1 = (a1 * m00 - a0 * m01) * inverse;
    int newC0 = to565(endpoint0);
    int newC1 = to565(endpoint1);
    if (determinant > 1e-6f)
    {
        c0 = max(newC0, newC1);
        c1 = min(newC0, newC1);
    }
}

int blockError(uint px[16], int c0, int c1)
{
    Palette p = makePalette(c0, c1);
    int error = 0;
    [unroll]
    for (int i2 = 0; i2 < 16; ++i2)
    {
        int3 c = rgbOf(px[i2]);
        int selector = selectorOf(p, c);
        int3 q = selector == 0 ? p.e0 : selector == 1 ? p.e1 : selector == 2 ? p.e2 : p.e3;
        int3 d = c - q;
        error += idot(d, d);
    }
    return error;
}

uint2 encodeSolid(int3 sum)
{
    int high, low;
    solidEndpoints(sum, high, low);

    uint mask = 0xAAu;
    if (low == high)
    {
        if (low > 0)
        {
            low -= 1;
            mask = 0u;
        }
        else
        {
            low = 0;
            high = 1;
            mask = 0x55u;
        }
    }

    if (high < low)
    {
        int swapped = high;
        high = low;
        low = swapped;
        mask ^= 0x55u;
    }

    return uint2(uint(high) | (uint(low) << 16), mask * 0x01010101u);
}

uint2 encodeDetailed(uint px[16], int3 lo, int3 hi, int3 sum, int3 sumSquares, int3 sumCross)
{
    float cxx = float(16 * sumSquares.x - sum.x * sum.x);
    float cyy = float(16 * sumSquares.y - sum.y * sum.y);
    float czz = float(16 * sumSquares.z - sum.z * sum.z);
    float cxy = float(16 * sumCross.x - sum.x * sum.y);
    float cxz = float(16 * sumCross.y - sum.x * sum.z);
    float cyz = float(16 * sumCross.z - sum.y * sum.z);

    float3 axis = float3(hi - lo);
    [unroll]
    for (int i3 = 0; i3 < 4; ++i3)
    {
        float nr = axis.x * cxx + axis.y * cxy + axis.z * cxz;
        float ng = axis.x * cxy + axis.y * cyy + axis.z * cyz;
        float nb = axis.x * cxz + axis.y * cyz + axis.z * czz;
        axis = float3(nr, ng, nb);
    }

    float scale = 1024.0f / max(max(abs(axis.x), abs(axis.y)), max(abs(axis.z), 1e-3f));
    axis *= scale;
    float invLengthSquared = 1.0f / (axis.x * axis.x + axis.y * axis.y + axis.z * axis.z + 0.0000125f);

    float3 mean = float3(sum) * (1.0f / 16.0f);
    float minProjection = 1e30f;
    float maxProjection = -1e30f;
    [unroll]
    for (int i4 = 0; i4 < 16; ++i4)
    {
        float3 offset = float3(rgbOf(px[i4])) - mean;
        float projection = offset.x * axis.x + offset.y * axis.y + offset.z * axis.z;
        minProjection = min(minProjection, projection);
        maxProjection = max(maxProjection, projection);
    }

    float tLow = minProjection * invLengthSquared;
    float tHigh = maxProjection * invLengthSquared;
    float3 seedLow = mean + tLow * axis;
    float3 seedHigh = mean + tHigh * axis;
    int c0 = to565(seedLow);
    int c1 = to565(seedHigh);
    separate(c0, c1);

    [unroll]
    for (int r5 = 0; r5 < LeastSquaresRounds; ++r5)
        leastSquaresRound(px, c0, c1);
    separate(c0, c1);

    // Low-variance blocks collapse the PCA seed, so also try the omatch-solid of the average.
    int3 range = hi - lo;
    if (range.x + range.y + range.z < AverageVarianceThreshold)
    {
        int altHigh, altLow;
        solidEndpoints(sum, altHigh, altLow);
        separate(altHigh, altLow);
        leastSquaresRound(px, altHigh, altLow);
        separate(altHigh, altLow);
        if (blockError(px, altHigh, altLow) < blockError(px, c0, c1))
        {
            c0 = altHigh;
            c1 = altLow;
        }
    }

    Palette p = makePalette(c0, c1);
    uint selectors = 0u;
    [unroll]
    for (int i6 = 0; i6 < 16; ++i6)
        selectors |= uint(selectorOf(p, rgbOf(px[i6]))) << (2 * i6);

    return uint2(uint(c0) | (uint(c1) << 16), selectors);
}

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= pc.numBlocksX || tid.y >= pc.numBlocksY)
        return;

    uint px[16];
    loadBlockPacked(tid.x, tid.y, px);

    int3 lo = 255;
    int3 hi = 0;
    int3 sum = 0;
    int3 sumSquares = 0;
    int3 sumCross = 0;
    [unroll]
    for (int i7 = 0; i7 < 16; ++i7)
    {
        int3 c = rgbOf(px[i7]);
        lo = min(lo, c);
        hi = max(hi, c);
        sum += c;
        sumSquares += c * c;
        sumCross += int3(c.x * c.y, c.x * c.z, c.y * c.z);
    }

    uint2 block;
    if (all(lo == hi))
        block = encodeSolid(sum);
    else
        block = encodeDetailed(px, lo, hi, sum, sumSquares, sumCross);
    _Blocks.Store2(blockByteOffset(tid.xy, 8u), block);
}
