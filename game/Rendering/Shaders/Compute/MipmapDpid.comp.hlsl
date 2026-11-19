/*
 * BSD 3-Clause License
 *
 * Copyright (c) 2016, Nicolas Weber, Sandra C. Amend / GCC / TU-Darmstadt
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
    * Redistributions of source code must retain the above copyright
      notice, this list of conditions and the following disclaimer.
    * Redistributions in binary form must reproduce the above copyright
      notice, this list of conditions and the following disclaimer in the
      documentation and/or other materials provided with the distribution.
    * Neither the name of the <organization> nor the
      names of its contributors may be used to endorse or promote products
      derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
 * ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * HLSL/Unity version Copyright (c) 2024, Chelsea "Feilen" Jaggi and VRChat Inc
 * The following is also licensed under the BSD 3-Clause License.
 */

// GPU twin of DpidMipFilter, adapted from VRChat's PerceptualPostProcessor.compute.
// On deep levels groupsPerTexel groups share a texel via _Partials, summed by the "finish" entry points, so one group
// doesn't loop over most of mip 0.

[[vk::binding(0)]] Texture2D<float4> _Source;
[[vk::binding(1)]] RWByteAddressBuffer _Mips;
[[vk::binding(2)]] RWByteAddressBuffer _Guidance;
[[vk::binding(3)]] RWByteAddressBuffer _Partials;

struct PushConstants
{
    uint srcWidth;
    uint srcHeight;
    uint dstWidth;
    uint dstHeight;
    uint dstByteOffset;
    uint guidanceByteOffset;
    uint threadsPerTexel;
    uint sqrtLambda;
    uint groupsPerTexel;
    uint partialsByteOffset;
    uint premultiplied;
};
[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

static const uint GroupSize = 64;
static const float FltEps = 1.192092896e-07;

groupshared float4 sColor[GroupSize];
groupshared float sFactor[GroupSize];
groupshared float sFactorAlpha[GroupSize];

struct ColorFactor
{
    float4 color;
    float factor;
    float factorAlpha;
};

void Add(inout ColorFactor sum, float4 color, float factorColor, float factorAlpha)
{
    float premultFactor = factorColor * color.a;
    sum.color += color * float4(premultFactor, premultFactor, premultFactor, factorAlpha);
    sum.factor += premultFactor;
    sum.factorAlpha += factorAlpha;
}

float4 Normalized(ColorFactor sum, float4 fallback)
{
    float3 rgb = sum.factor < FltEps ? fallback.rgb : sum.color.rgb / sum.factor;
    float alpha = sum.factorAlpha < FltEps ? fallback.a : sum.color.a / sum.factorAlpha;
    return float4(rgb, alpha);
}

float SrgbToLinear(float c)
{
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

uint LinearToSrgbByte(float linear)
{
    float c = saturate(linear);
    float s = c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return (uint)(s * 255.0 + 0.5);
}

uint Pack(float4 linear)
{
    return LinearToSrgbByte(linear.r)
        | (LinearToSrgbByte(linear.g) << 8)
        | (LinearToSrgbByte(linear.b) << 16)
        | ((uint)(saturate(linear.a) * 255.0 + 0.5) << 24);
}

float4 UnpackGuidance(uint2 p)
{
    uint packed = _Guidance.Load(pc.guidanceByteOffset + (p.y * pc.dstWidth + p.x) * 4u);
    float4 c = float4(packed & 0xffu, (packed >> 8) & 0xffu, (packed >> 16) & 0xffu, packed >> 24) * (1.0 / 255.0);
    return float4(SrgbToLinear(c.r), SrgbToLinear(c.g), SrgbToLinear(c.b), c.a);
}

// DpidMipFilter.Unpremultiply: the filter weighs colour by alpha itself, so a premultiplied source runs on its straight colour.
float4 LoadLinear(uint2 p)
{
    float4 c = _Source.Load(int3(p, 0));
    uint4 b = uint4(round(c * 255.0));
    if (pc.premultiplied != 0 && b.a != 0)
        c.rgb = float3(min(255u, (b.rgb * 255u + b.a / 2u) / b.a)) * (1.0 / 255.0);
    return float4(SrgbToLinear(c.r), SrgbToLinear(c.g), SrgbToLinear(c.b), c.a);
}

uint Premultiply(uint packed)
{
    uint4 b = uint4(packed & 0xffu, (packed >> 8) & 0xffu, (packed >> 16) & 0xffu, packed >> 24);
    b.rgb = (b.rgb * b.a + 127u) / 255u;
    return b.r | (b.g << 8) | (b.b << 16) | (b.a << 24);
}

float Contribution(float2 low, float2 high, uint2 p)
{
    float f = 1.0;
    float2 fp = float2(p);
    if (fp.x < low.x) f *= 1.0 - (low.x - fp.x);
    if (fp.x + 1.0 > high.x) f *= 1.0 - (fp.x + 1.0 - high.x);
    if (fp.y < low.y) f *= 1.0 - (low.y - fp.y);
    if (fp.y + 1.0 > high.y) f *= 1.0 - (fp.y + 1.0 - high.y);
    return f;
}

float4 BoxBlur(uint2 p)
{
    ColorFactor avg = (ColorFactor)0;
    for (int dy = -1; dy <= 1; dy++)
    {
        for (int dx = -1; dx <= 1; dx++)
        {
            int2 q = int2(p) + int2(dx, dy);
            if (q.x < 0 || q.y < 0 || q.x >= int(pc.dstWidth) || q.y >= int(pc.dstHeight))
                continue;
            float weight = (dx == 0 ? 2.0 : 1.0) * (dy == 0 ? 2.0 : 1.0);
            Add(avg, UnpackGuidance(uint2(q)), weight, weight);
        }
    }
    return Normalized(avg, avg.color);
}

ColorFactor Patch(uint2 texel, bool inside, uint lane, uint part, uint index, bool guidancePass, float4 avg)
{
    float2 patchSize = float2(pc.srcWidth, pc.srcHeight) / float2(pc.dstWidth, pc.dstHeight);
    float2 low = max(float2(texel) * patchSize, 0.0);
    float2 high = min((float2(texel) + 1.0) * patchSize, float2(pc.srcWidth, pc.srcHeight));
    uint2 first = uint2(floor(low));
    uint2 size = uint2(ceil(high)) - first;
    uint count = inside ? size.x * size.y : 0;

    ColorFactor sum = (ColorFactor)0;
    for (uint i = part * pc.threadsPerTexel + lane; i < count; i += pc.threadsPerTexel * pc.groupsPerTexel)
    {
        uint2 p = first + uint2(i % size.x, i / size.x);
        float4 pixel = LoadLinear(p);
        float contribution = Contribution(low, high, p);
        float fc = contribution;
        float fa = contribution;
        if (!guidancePass)
        {
            float4 d = pixel - avg;
            fc = length(d.rgb) / sqrt(3.0);
            fa = abs(d.a);
            if (pc.sqrtLambda != 0)
            {
                fc = sqrt(fc);
                fa = sqrt(fa);
            }
            fc *= contribution;
            fa *= contribution;
        }
        Add(sum, pixel, fc, fa);
    }

    sColor[index] = sum.color;
    sFactor[index] = sum.factor;
    sFactorAlpha[index] = sum.factorAlpha;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = pc.threadsPerTexel / 2; stride > 0; stride /= 2)
    {
        if (lane < stride)
        {
            sColor[index] += sColor[index + stride];
            sFactor[index] += sFactor[index + stride];
            sFactorAlpha[index] += sFactorAlpha[index + stride];
        }
        GroupMemoryBarrierWithGroupSync();
    }

    sum.color = sColor[index];
    sum.factor = sFactor[index];
    sum.factorAlpha = sFactorAlpha[index];
    return sum;
}

uint2 Texel(uint3 group, uint index)
{
    return uint2(group.x / pc.groupsPerTexel * (GroupSize / pc.threadsPerTexel) + index / pc.threadsPerTexel, group.y);
}

uint PartialAddress(uint2 texel, uint part)
{
    return pc.partialsByteOffset + ((texel.y * pc.dstWidth + texel.x) * pc.groupsPerTexel + part) * 32u;
}

void StorePartial(uint2 texel, uint part, ColorFactor sum)
{
    uint address = PartialAddress(texel, part);
    _Partials.Store4(address, asuint(sum.color));
    _Partials.Store2(address + 16u, asuint(float2(sum.factor, sum.factorAlpha)));
}

ColorFactor SumPartials(uint2 texel)
{
    ColorFactor sum = (ColorFactor)0;
    for (uint part = 0; part < pc.groupsPerTexel; part++)
    {
        uint address = PartialAddress(texel, part);
        sum.color += asfloat(_Partials.Load4(address));
        float2 factors = asfloat(_Partials.Load2(address + 16u));
        sum.factor += factors.x;
        sum.factorAlpha += factors.y;
    }
    return sum;
}

void StoreGuidance(uint2 texel, ColorFactor sum)
{
    _Guidance.Store(pc.guidanceByteOffset + (texel.y * pc.dstWidth + texel.x) * 4u, Pack(Normalized(sum, float4(1, 0, 1, 1))));
}

void StoreMip(uint2 texel, ColorFactor sum, float4 avg)
{
    uint packed = Pack(Normalized(sum, avg));
    _Mips.Store(pc.dstByteOffset + (texel.y * pc.dstWidth + texel.x) * 4u, pc.premultiplied != 0 ? Premultiply(packed) : packed);
}

[numthreads(64, 1, 1)]
void guidance(uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    uint2 texel = Texel(group, index);
    bool inside = texel.x < pc.dstWidth;
    uint lane = index % pc.threadsPerTexel;
    uint part = group.x % pc.groupsPerTexel;
    ColorFactor sum = Patch(texel, inside, lane, part, index, true, 0.0);
    if (!inside || lane != 0)
        return;
    if (pc.groupsPerTexel > 1)
        StorePartial(texel, part, sum);
    else
        StoreGuidance(texel, sum);
}

[numthreads(64, 1, 1)]
void downsample(uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    uint2 texel = Texel(group, index);
    bool inside = texel.x < pc.dstWidth;
    uint lane = index % pc.threadsPerTexel;
    uint part = group.x % pc.groupsPerTexel;
    float4 avg = inside ? BoxBlur(texel) : 0.0;
    ColorFactor sum = Patch(texel, inside, lane, part, index, false, avg);
    if (!inside || lane != 0)
        return;
    if (pc.groupsPerTexel > 1)
        StorePartial(texel, part, sum);
    else
        StoreMip(texel, sum, avg);
}

[numthreads(64, 1, 1)]
void guidanceFinish(uint3 id : SV_DispatchThreadID)
{
    uint2 texel = uint2(id.x % pc.dstWidth, id.x / pc.dstWidth);
    if (texel.y < pc.dstHeight)
        StoreGuidance(texel, SumPartials(texel));
}

[numthreads(64, 1, 1)]
void downsampleFinish(uint3 id : SV_DispatchThreadID)
{
    uint2 texel = uint2(id.x % pc.dstWidth, id.x / pc.dstWidth);
    if (texel.y < pc.dstHeight)
        StoreMip(texel, SumPartials(texel), BoxBlur(texel));
}
