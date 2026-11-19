#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

float grayscale(vec4 color)
{
    return color.r * 0.2126 + color.g * 0.7152 + color.b * 0.0722;
}

float safeAlpha(vec2 coord)
{
    vec2 s = clamp(coord, 0.0, 1.0);
    if (s.x != coord.x || s.y != coord.y)
        return 0.0;
    return texture(_MainTex, coord).a;
}

float safeGray(vec2 coord)
{
    vec2 s = clamp(coord, 0.0, 1.0);
    if (s.x != coord.x || s.y != coord.y)
        return 0.0;
    return grayscale(texture(_MainTex, coord));
}

void main()
{
    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec2 texel = 1.0 / vec2(textureSize(_MainTex, 0));

    vec4 bodyColor = texture(_MainTex, uv);
    if (bodyColor.a - 0.5 < 0.0)
        discard;
    float bodyGray = grayscale(bodyColor);

    float intensity = 0.0;
    float intensityInner = 0.0;

    // Outer edge detection.
    {
        const int expansion = 3;
        float mina = 1.0;
        float maxa = 0.0;
        for (int ang = 0; ang < 8; ++ang)
        {
            float rang = 3.14159 * float(ang) / 4.0;
            float a = safeAlpha(uv + vec2(sin(rang) * texel.x * float(expansion), cos(rang) * texel.y * float(expansion)));
            mina = min(mina, a);
            maxa = max(maxa, a);
        }
        intensity = max(intensity, maxa - mina);
    }

    // Inner edge detection.
    {
        const int expansion = 4;
        const float intensityMult = 35.0;

        float minx = safeGray(uv - vec2(texel.x, 0.0) * float(expansion));
        float maxx = safeGray(uv + vec2(texel.x, 0.0) * float(expansion));
        float leastx = min(minx, maxx);

        float miny = safeGray(uv - vec2(0.0, texel.y) * float(expansion));
        float maxy = safeGray(uv + vec2(0.0, texel.y) * float(expansion));
        float leasty = min(miny, maxy);

        if (bodyGray < leastx)
            intensityInner = max(intensityInner, clamp((leastx - bodyGray) * intensityMult, 0.0, 1.0));
        if (bodyGray < leasty)
            intensityInner = max(intensityInner, clamp((leasty - bodyGray) * intensityMult, 0.0, 1.0));

        intensityInner *= 0.70;
    }

    // Minimum internal intensity creates a glow across the whole blueprint, giving it solidity.
    intensityInner = max(intensityInner, 0.45);

    vec4 outer = vec4(intensity, intensity, intensity, mix(bodyColor.a * 0.3, 1.0, intensity));
    vec4 inner = vec4(intensityInner, intensityInner, intensityInner, mix(bodyColor.a * 0.3, 1.0, intensityInner));
    outColor = max(outer, inner) * uMaterial._Color * vColor;
}
