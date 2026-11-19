#include "BC4Encode.hlsl"

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= pc.numBlocksX || tid.y >= pc.numBlocksY)
        return;

    uint px[16];
    loadBlockPacked(tid.x, tid.y, px);

    _Blocks.Store2(blockByteOffset(tid.xy, 8u), encodeBc4(px, 0u));
}
