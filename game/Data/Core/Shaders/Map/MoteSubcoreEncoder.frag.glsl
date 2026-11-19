#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;        // @default = 0.0
    float _ScanInterval;   // @default = 1.0
    float _BaseIntensity;  // @default = 1.0
    float _ScanIntensity;  // @default = 1.0
    float _ScanFalloffPow; // @default = 4.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DetailTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 color = texture(_MainTex, flipV(vUv));
    vec4 detail = texture(_DetailTex, flipV(vUv));

    float frac = mod(u._AgeSecs, u._ScanInterval) / u._ScanInterval;
    bool sign = true;

    if (frac > 0.5) // ping pong the value
    {
        frac = 0.5 - (frac - 0.5);
        sign = false;
    }

    frac *= 2.0;

    float diff = abs(frac - vUv.x);

    if (sign)
    {
        if (frac < vUv.x)
            diff = 1.0;
    }
    else
    {
        if (frac > vUv.x)
            diff = 1.0;
    }

    float scanIntensity = max(pow(1.0 - max(diff, 0.0), u._ScanFalloffPow) - detail.r, 0.0);

    vec4 f = u._Color * (color * u._BaseIntensity + color * scanIntensity * u._ScanIntensity);
    outColor = clamp(f, 0.0, 1.0);
}
