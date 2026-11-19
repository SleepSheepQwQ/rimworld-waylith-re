// Castano's coverage-preserving mipmaps, as a direct inverse-CDF lookup instead of his binary search.

[[vk::binding(0)]] RWByteAddressBuffer _Hist;
[[vk::binding(1)]] RWByteAddressBuffer _Scales;

struct PushConstants
{
    uint baseWidth;
    uint baseHeight;
    uint mipCount;
    float cutoff;
};
[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

uint binCount(uint mip, uint bin) { return _Hist.Load((mip * 256u + bin) * 4u); }

[numthreads(1, 1, 1)]
void main()
{
    uint total0 = max(pc.baseWidth, 1u) * max(pc.baseHeight, 1u);
    uint cutoffBin = min(uint(pc.cutoff * 255.0 + 0.5), 255u);

    // Bin 0 is left out of coverage: atlas padding bleeds into sprite edges as mips coarsen, so counting it
    // would scale cutouts down and fade grass out instead of preserving it.
    uint refCount = 0u;
    for (uint b = cutoffBin; b <= 255u; ++b)
        refCount += binCount(0u, b);
    uint live0 = total0 - binCount(0u, 0u);
    float desiredCoverage = (live0 > 0u) ? float(refCount) / float(live0) : 0.0;

    _Scales.Store(0u, asuint(1.0));
    for (uint mip = 1u; mip <= pc.mipCount; ++mip)
    {
        float scale = 1.0;
        if (refCount > 0u)
        {
            uint w = max(pc.baseWidth >> mip, 1u);
            uint h = max(pc.baseHeight >> mip, 1u);
            uint liveN = (w * h) - binCount(mip, 0u);
            float target = desiredCoverage * float(liveN);

            uint acc = 0u;
            uint t = 1u;
            for (int b = 255; b >= 1; --b)
            {
                acc += binCount(mip, uint(b));
                if (float(acc) >= target)
                {
                    t = uint(b);
                    break;
                }
            }

            float q = float(t) * (1.0 / 255.0);
            scale = clamp((q > 1e-4) ? (pc.cutoff / q) : 4.0, 0.0, 4.0);
        }
        _Scales.Store(mip * 4u, asuint(scale));
    }
}
