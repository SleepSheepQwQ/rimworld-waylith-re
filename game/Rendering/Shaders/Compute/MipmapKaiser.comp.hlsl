// GPU twin of KaiserMipFilter; keep the filter and colour handling identical so GPU and CPU chains match.

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

static const float FilterWidth = 2.0;
static const float KaiserAlpha = 4.0;
static const float Pi = 3.14159265358979;
static const int MaxTaps = 16; // halving keeps the scale under 3, so at most 13 taps

float Bessel0(float x)
{
    float sum = 1.0;
    float term = 1.0;
    float halfX = x * 0.5;
    for (int k = 1; k < 24; k++)
    {
        term *= halfX / k * (halfX / k);
        sum += term;
        if (term < 1e-7 * sum)
            break;
    }
    return sum;
}

float Kaiser(float x, float besselAlpha)
{
    float ratio = x / FilterWidth;
    float inside = 1.0 - ratio * ratio;
    if (inside <= 0.0)
        return 0.0;
    float sinc = abs(Pi * x) < 1e-4 ? 1.0 : sin(Pi * x) / (Pi * x);
    return sinc * Bessel0(KaiserAlpha * sqrt(inside)) / besselAlpha;
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

float4 LoadLinear(int2 p)
{
    p = clamp(p, int2(0, 0), int2(int(pc.srcWidth) - 1, int(pc.srcHeight) - 1));
    float4 c;
    if (pc.readFromImage != 0)
    {
        c = _Source.Load(int3(p, 0));
    }
    else
    {
        uint packed = _Mips.Load(pc.srcByteOffset + (uint(p.y) * pc.srcWidth + uint(p.x)) * 4u);
        c = float4(packed & 0xffu, (packed >> 8) & 0xffu, (packed >> 16) & 0xffu, (packed >> 24) & 0xffu) * (1.0 / 255.0);
    }
    if (pc.premultiplied == 0)
        return float4(SrgbToLinear(c.r), SrgbToLinear(c.g), SrgbToLinear(c.b), c.a);

    // KaiserMipFilter: straight colour as DpidMipFilter.Unpremultiply rounds it, to linear, then times alpha.
    uint4 b = uint4(round(c * 255.0));
    if (b.a != 0)
        c.rgb = float3(min(255u, (b.rgb * 255u + b.a / 2u) / b.a)) * (1.0 / 255.0);
    return float4(SrgbToLinear(c.r) * c.a, SrgbToLinear(c.g) * c.a, SrgbToLinear(c.b) * c.a, c.a);
}

void Taps(uint sourceSize, uint destinationSize, uint i, out int first, out int count, out float scale, out float centre)
{
    scale = max(1.0, (float)sourceSize / (float)destinationSize);
    float radius = FilterWidth * scale;
    int maxCount = min((int)ceil(radius * 2.0) + 1, MaxTaps);
    centre = (i + 0.5) * (float)sourceSize / (float)destinationSize - 0.5;
    first = (int)ceil(centre - radius);
    int last = (int)floor(centre + radius);
    count = min(maxCount, last - first + 1);
}

[numthreads(8, 8, 1)]
void main(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= pc.dstWidth || tid.y >= pc.dstHeight)
        return;

    int firstX, countX, firstY, countY;
    float scaleX, centreX, scaleY, centreY;
    Taps(pc.srcWidth, pc.dstWidth, tid.x, firstX, countX, scaleX, centreX);
    Taps(pc.srcHeight, pc.dstHeight, tid.y, firstY, countY, scaleY, centreY);

    float besselAlpha = Bessel0(KaiserAlpha);
    float weightsX[MaxTaps];
    float totalX = 0.0;
    for (int tx = 0; tx < countX; tx++)
    {
        weightsX[tx] = Kaiser((firstX + tx - centreX) / scaleX, besselAlpha);
        totalX += weightsX[tx];
    }

    float4 sum = 0.0;
    float totalY = 0.0;
    for (int ty = 0; ty < countY; ty++)
    {
        float weightY = Kaiser((firstY + ty - centreY) / scaleY, besselAlpha);
        totalY += weightY;
        float4 row = 0.0;
        for (int tx = 0; tx < countX; tx++)
            row += LoadLinear(int2(firstX + tx, firstY + ty)) * weightsX[tx];
        sum += row * weightY;
    }
    sum /= totalX * totalY;

    uint alpha = (uint)(saturate(sum.a) * 255.0 + 0.5);
    uint3 rgb;
    if (pc.premultiplied == 0)
    {
        rgb = uint3(LinearToSrgbByte(sum.r), LinearToSrgbByte(sum.g), LinearToSrgbByte(sum.b));
    }
    else
    {
        float3 straight = alpha == 0 ? float3(0.0, 0.0, 0.0) : sum.rgb / sum.a;
        rgb = (uint3(LinearToSrgbByte(straight.r), LinearToSrgbByte(straight.g), LinearToSrgbByte(straight.b)) * alpha + 127u) / 255u;
    }
    uint packed = rgb.r | (rgb.g << 8) | (rgb.b << 16) | (alpha << 24);
    _Mips.Store(pc.dstByteOffset + (tid.y * pc.dstWidth + tid.x) * 4u, packed);
}
