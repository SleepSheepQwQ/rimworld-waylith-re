// GPU performance probe kernels - micro-benchmarks for the boot-time hardware-tier harness
// (Waylith/Hardware/GpuProbe.cs).
//
//     AluProbeCS        - ALU/compute throughput. A runtime-length FMA loop over per-thread registers
//                         with eight independent accumulators (enough ILP to fill the pipelines), one
//                         store per thread. Memory traffic is one float per thread, so for a large
//                         iteration count the cost is dominated by arithmetic.
//
//     BandwidthProbeCS  - memory bandwidth. A grid-stride streaming read that touches every element of a
//                         buffer larger than any GPU last-level cache EXACTLY ONCE, accumulated and stored
//                         once per thread; bytes-read / time = GB/s. It deliberately does NOT re-read the
//                         buffer: a repeat would be served from L2.
//
// The loop trip count and element count come from push constants (runtime values),
// so the compiler cannot precompute the result or unroll the loop away.

#define THREAD_GROUP_SIZE 64

struct PushConstants
{
    uint Iterations;    // AluProbe: FMA loop trip count
    uint ElementCount;  // BandwidthProbe: number of floats to stream from g_In
    uint ThreadStride;  // BandwidthProbe: total thread count
    uint Pad;
};
[[vk::push_constant]] ConstantBuffer<PushConstants> pc;

[[vk::binding(0)]] RWStructuredBuffer<float> g_Out; // one folded result per thread
[[vk::binding(1)]] RWStructuredBuffer<float> g_In;  // BandwidthProbe source; unused by AluProbe (still bound)

[numthreads(THREAD_GROUP_SIZE, 1, 1)]
void AluProbeCS(uint3 tid : SV_DispatchThreadID)
{
    uint i = tid.x;

    // Eight fully independent chains so a single wave can fill the FMA pipelines (dependent chains
    // would measure latency). mad() guarantees a real fused Fma instruction (a * b + c is emitted as
    // separate mul + add and fusing is left to the driver). The damped form x = x * R + C converges to
    // C / (1 - R), so values stay finite for any iteration count (never Inf, never denormal).
    float a0 = (float)i * 0.123f + 1.0f;
    float a1 = (float)i * 0.456f + 2.0f;
    float a2 = (float)i * 0.789f + 3.0f;
    float a3 = (float)i * 0.321f + 4.0f;
    float a4 = (float)i * 0.654f + 5.0f;
    float a5 = (float)i * 0.987f + 6.0f;
    float a6 = (float)i * 0.213f + 7.0f;
    float a7 = (float)i * 0.546f + 8.0f;

    uint iterations = pc.Iterations;
    for (uint k = 0; k < iterations; k++)
    {
        a0 = mad(a0, 0.9999f, 0.1f);
        a1 = mad(a1, 0.9999f, 0.2f);
        a2 = mad(a2, 0.9999f, 0.3f);
        a3 = mad(a3, 0.9999f, 0.4f);
        a4 = mad(a4, 0.9999f, 0.5f);
        a5 = mad(a5, 0.9999f, 0.6f);
        a6 = mad(a6, 0.9999f, 0.7f);
        a7 = mad(a7, 0.9999f, 0.8f);
    }

    g_Out[i] = a0 + a1 + a2 + a3 + a4 + a5 + a6 + a7; // always written -> the whole loop is live
}

[numthreads(THREAD_GROUP_SIZE, 1, 1)]
void BandwidthProbeCS(uint3 tid : SV_DispatchThreadID)
{
    uint gid = tid.x;
    uint n = pc.ElementCount;
    uint stride = pc.ThreadStride;

    // Single grid-stride pass: consecutive threads read consecutive addresses (coalesced), and the whole
    // buffer (> L2) is touched exactly once, so we don't measure cache.
    float acc = 0.0f;
    for (uint idx = gid; idx < n; idx += stride)
        acc += g_In[idx];

    g_Out[gid] = acc; // anti-DCE sink
}
