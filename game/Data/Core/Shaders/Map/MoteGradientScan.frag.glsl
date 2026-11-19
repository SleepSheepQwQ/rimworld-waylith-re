#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;        // @default = 0.0
    float _ScanInterval;   // @default = 1.0
    float _ScanFalloffPow; // @default = 4.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec4 color = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy);

    if (color.a < 0.5)
        discard;

    float frac = mod(u._AgeSecs + color.r, u._ScanInterval) / u._ScanInterval;

    frac *= 2.0;

    float scanIntensity = max(pow(abs(frac - 1.0), u._ScanFalloffPow), 0.0);

    outColor = clamp(u._Color * scanIntensity, 0.0, 1.0);
}
