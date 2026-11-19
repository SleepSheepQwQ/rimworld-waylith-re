[[vk::binding(0)]] RWByteAddressBuffer _Mips;
[[vk::binding(1)]] RWByteAddressBuffer _Scales;

struct PushConstants
{
    uint byteOffset;
    uint width;
    uint height;
    uint mipIndex;
};
[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= pc.width || tid.y >= pc.height)
        return;

    uint addr = pc.byteOffset + (tid.y * pc.width + tid.x) * 4u;
    uint packed = _Mips.Load(addr);

    float scale = asfloat(_Scales.Load(pc.mipIndex * 4u));
    float a = saturate(float((packed >> 24) & 0xffu) * (1.0 / 255.0) * scale);

    uint aq = min(uint(a * 255.0 + 0.5), 255u);
    _Mips.Store(addr, (packed & 0x00ffffffu) | (aq << 24));
}
