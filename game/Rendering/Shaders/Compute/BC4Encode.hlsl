// Derived from basis_universal (https://github.com/BinomialLLC/basis_universal), Copyright (C) 2016-2026 Binomial LLC.
// Licensed under the Apache License, Version 2.0. Modified from the original.

// encodeBc4 ports Bc4BlockEncoder, bit-identical to it. The refit's one division is done exactly in integers because
// OpFDiv only promises 2.5 ULP, and each float step after it goes through keep() so no fma contraction or fast-math
// reassociation moves a rounding. Not precise: MoltenVK drops fast math for a whole kernel with any precise op, 38%
// slower on an M1.
//
// Loop counters are numbered because slang's HLSL mode hoists a for-loop declaration to function scope.

#include "BCEncodeCommon.hlsl"

// float(1.0 / 7.0), as the CPU encoder's constant rounds.
static const float OneSeventh = asfloat(0x3E124925u);

// An opaque identity: numBlocksX is under 2^31, so the xor is with zero, which the compiler cannot know, and so
// cannot fold the float operations on either side of it into one.
float keep(float x)
{
    return asfloat(asuint(x) ^ (pc.numBlocksX >> 31));
}

int roundHalfEven(float x)
{
    float f = floor(x);
    float d = keep(x - f);
    int i = int(f);
    return (d > 0.5f || (d == 0.5f && (i & 1) != 0)) ? i + 1 : i;
}

// Correctly rounded like IEEE division. |numerator| < 2^24 and 0 < denominator < 2^15, so the long division
// stays in 32 bits and the integer quotient never needs shifting down.
float divideExact(int numerator, int denominator)
{
    if (numerator == 0)
        return 0.0f;

    uint magnitude = uint(abs(numerator));
    uint divisor = uint(denominator);
    uint mantissa = magnitude / divisor;
    uint remainder = magnitude - mantissa * divisor;
    int fractionBits = 0;

    // 16 then 8 bits at a time keeps each shift under 2^32; 25 bits are a float's 24 plus the rounding bit.
    while (mantissa < (1u << 16))
    {
        remainder <<= 16;
        uint digits = remainder / divisor;
        mantissa = (mantissa << 16) | digits;
        remainder -= digits * divisor;
        fractionBits += 16;
    }
    while (mantissa < (1u << 24))
    {
        remainder <<= 8;
        uint digits = remainder / divisor;
        mantissa = (mantissa << 8) | digits;
        remainder -= digits * divisor;
        fractionBits += 8;
    }

    uint excess = firstbithigh(mantissa) - 24u;
    bool sticky = remainder != 0u || (mantissa & ((1u << excess) - 1u)) != 0u;
    mantissa >>= excess;
    fractionBits -= int(excess);

    bool roundUp = (mantissa & 1u) != 0u && (sticky || (mantissa & 2u) != 0u);
    mantissa = (mantissa >> 1) + (roundUp ? 1u : 0u);
    --fractionBits;
    if (mantissa == (1u << 24))
    {
        mantissa >>= 1;
        --fractionBits;
    }

    uint bits = (uint(127 + 23 - fractionBits) << 23) | (mantissa & 0x7FFFFFu);
    return numerator < 0 ? -asfloat(bits) : asfloat(bits);
}

struct Evaluation
{
    int error;
    uint low;
    uint high;
    int sumRank, sumRankSquared, sumRankValue;
};

// Requires r0 > r1 (8-value mode). Rank is the interpolation weight towards r0, 0..7.
Evaluation evaluate(int v[16], int r0, int r1)
{
    int delta = r0 - r1;
    int bias = 4 - r1 * 14;
    Evaluation e;
    e.error = 0;
    e.low = 0u;
    e.high = 0u;
    e.sumRank = 0;
    e.sumRankSquared = 0;
    e.sumRankValue = 0;

    [unroll]
    for (int i0 = 0; i0 < 16; ++i0)
    {
        int scaled = v[i0] * 14 + bias;
        int rank = int(scaled >= delta * 13) + int(scaled >= delta * 11) + int(scaled >= delta * 9) + int(scaled >= delta * 7)
            + int(scaled >= delta * 5) + int(scaled >= delta * 3) + int(scaled >= delta);
        uint index = rank == 7 ? 0u : rank == 0 ? 1u : uint(8 - rank);
        if (i0 < 10)
            e.low |= index << (3 * i0);
        else
            e.high |= index << (3 * (i0 - 10));

        int reconstructed = int((uint(rank * r0 + (7 - rank) * r1) * 9363u) >> 16);
        int difference = v[i0] - reconstructed;
        e.error += difference * difference;
        e.sumRank += rank;
        e.sumRankSquared += rank * rank;
        e.sumRankValue += rank * v[i0];
    }

    return e;
}

uint2 encodeBc4(uint px[16], uint shift)
{
    int v[16];
    int lo = 255;
    int hi = 0;
    int sum = 0;
    [unroll]
    for (int i1 = 0; i1 < 16; ++i1)
    {
        v[i1] = int((px[i1] >> shift) & 255u);
        lo = min(lo, v[i1]);
        hi = max(hi, v[i1]);
        sum += v[i1];
    }

    int r0, r1;
    uint low, high;
    if (lo == hi)
    {
        uint index = hi > 0 ? 0u : 1u;
        r0 = hi > 0 ? hi : 1;
        r1 = hi > 0 ? hi - 1 : 0;
        low = index * 0x9249249u;
        high = index * 0x9249u;
    }
    else
    {
        Evaluation a = evaluate(v, hi, lo);
        r0 = hi;
        r1 = lo;
        low = a.low;
        high = a.high;

        int determinant = 16 * a.sumRankSquared - a.sumRank * a.sumRank;
        if (determinant != 0)
        {
            float b = divideExact(7 * (16 * a.sumRankValue - a.sumRank * sum), determinant);
            float bSumRankOver7 = keep(keep(b * float(a.sumRank)) * OneSeventh);
            float intercept = keep(float(sum) - bSumRankOver7) * 0.0625f;
            float slopeEnd = keep(intercept + b);
            int refit1 = clamp(roundHalfEven(intercept), 0, 255);
            int refit0 = clamp(roundHalfEven(slopeEnd), 0, 255);
            if (refit0 > refit1)
            {
                Evaluation e = evaluate(v, refit0, refit1);
                if (e.error < a.error)
                {
                    r0 = refit0;
                    r1 = refit1;
                    low = e.low;
                    high = e.high;
                }
            }
        }
    }

    return uint2(uint(r0) | (uint(r1) << 8) | ((low & 0xFFFFu) << 16), (low >> 16) | (high << 14));
}
