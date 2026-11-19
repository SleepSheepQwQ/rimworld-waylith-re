[[vk::binding(0)]] Texture2D<float4> _Source;
[[vk::binding(1)]] RWByteAddressBuffer _Mips;
[[vk::binding(2)]] RWByteAddressBuffer _Hist;

struct PushConstants
{
    uint readFromImage;
    uint srcByteOffset;
    uint width;
    uint height;
    uint histUintOffset;
};
[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= pc.width || tid.y >= pc.height)
        return;

    float a;
    if (pc.readFromImage != 0)
        a = _Source.Load(int3(int(tid.x), int(tid.y), 0)).a;
    else
        a = float((_Mips.Load(pc.srcByteOffset + (tid.y * pc.width + tid.x) * 4u) >> 24) & 0xffu) * (1.0 / 255.0);

    uint bin = min(uint(a * 255.0 + 0.5), 255u);
    _Hist.InterlockedAdd((pc.histUintOffset + bin) * 4u, 1u);
}
