[[vk::binding(0)]] Texture2D<float4> _Source;
[[vk::binding(1)]] RWByteAddressBuffer _Blocks;

struct PushConstants
{
    uint numBlocksX;
    uint numBlocksY;
    uint outBlockOffset;
    uint srcMip;
    uint mipWidth;
    uint mipHeight;
    float quality;
    uint flags;
    uint srcBlockRowOffset; // the dispatch covers a band of block rows, numBlocksY tall
};
[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

// Exact only because every caller binds a UNORM8 view; an sRGB view would linearise in .Load.
void loadBlockPacked(uint bx, uint by, out uint px[16])
{
    int mip = int(pc.srcMip);
    int maxX = int(pc.mipWidth) - 1;
    int maxY = int(pc.mipHeight) - 1;
    [unroll]
    for (uint i0 = 0; i0 < 16; ++i0)
    {
        int x = clamp(int(bx * 4u + (i0 & 3u)), 0, maxX);
        int y = clamp(int((by + pc.srcBlockRowOffset) * 4u + (i0 >> 2u)), 0, maxY);
        uint4 t = uint4(round(_Source.Load(int3(x, y, mip)) * 255.f));
        px[i0] = t.r | (t.g << 8) | (t.b << 16) | (t.a << 24);
    }
}

uint blockByteOffset(uint2 block, uint blockBytes)
{
    return (pc.outBlockOffset + block.y * pc.numBlocksX + block.x) * blockBytes;
}
