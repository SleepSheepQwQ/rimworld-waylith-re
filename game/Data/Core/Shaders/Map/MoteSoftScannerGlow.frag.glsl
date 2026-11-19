#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;          // @default = 0.0
    float _ScanSpeed;        // @default = 1.0
    float _ScanPow;          // @default = 1.0
    float _FlipHorizontally; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 uv = vUv;
    if (u._FlipHorizontally > 0.0)
        uv.x = 1.0 - uv.x;

    vec4 tex = texture(_MainTex, flipV(uv));

    float scanTarget = 2.0 - mod(u._AgeSecs * u._ScanSpeed, 2.0) - 0.5;
    float scanFinal = (0.5 - pow(scanTarget - tex.r, 2.0)) * 2.0;

    outColor = clamp(tex.a * pow(scanFinal, u._ScanPow) * u._Color, 0.0, 1.0);
}
