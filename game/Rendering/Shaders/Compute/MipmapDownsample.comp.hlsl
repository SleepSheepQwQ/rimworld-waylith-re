// RGB is weighted by alpha so transparent texels don't bleed their (often black) colour into the mip.
// Premultiplied colour already carries that weight; weighting it again leaves edges brighter than their alpha.

[[vk::binding(0)]] Texture2D<float4> _Source;
[[vk::binding(1)]] RWByteAddressBuffer _Mips;

struct PushConstants
{
    uint readFromImage;
    uint srcByteOffset;
    uint dstByteOffset;
    uint srcWidth;
    uint srcHeight;
    uint dstWidth;
    uint dstHeight;
    uint premultiplied;
};
[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

float4 loadSource(int2 p)
{
    p = clamp(p, int2(0, 0), int2(int(pc.srcWidth) - 1, int(pc.srcHeight) - 1));
    if (pc.readFromImage != 0)
        return _Source.Load(int3(p, 0));

    uint packed = _Mips.Load(pc.srcByteOffset + (uint(p.y) * pc.srcWidth + uint(p.x)) * 4u);
    return float4(packed & 0xffu, (packed >> 8) & 0xffu, (packed >> 16) & 0xffu, (packed >> 24) & 0xffu) * (1.0 / 255.0);
}

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= pc.dstWidth || tid.y >= pc.dstHeight)
        return;

    int2 s = int2(tid.xy) * 2;
    float4 c0 = loadSource(s + int2(0, 0));
    float4 c1 = loadSource(s + int2(1, 0));
    float4 c2 = loadSource(s + int2(0, 1));
    float4 c3 = loadSource(s + int2(1, 1));

    float aSum = c0.a + c1.a + c2.a + c3.a;
    float3 rgb = pc.premultiplied == 0 && aSum > 1e-5
        ? (c0.rgb * c0.a + c1.rgb * c1.a + c2.rgb * c2.a + c3.rgb * c3.a) / aSum
        : (c0.rgb + c1.rgb + c2.rgb + c3.rgb) * 0.25;
    float a = aSum * 0.25;

    uint4 q = (uint4)(saturate(float4(rgb, a)) * 255.0 + 0.5);
    uint packed = q.x | (q.y << 8) | (q.z << 16) | (q.w << 24);
    _Mips.Store(pc.dstByteOffset + (tid.y * pc.dstWidth + tid.x) * 4u, packed);
}
