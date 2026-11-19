#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Color2;     // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;  // @default = 0.0
    float _Interval; // @default = 0.1
    float _PulseMin; // @default = -1.0
    float _PulseMax; // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec4 tex = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy);
    float pulse = u._PulseMin + (sin(u._AgeSecs / u._Interval) + 1.0) * 0.5 * (u._PulseMax - u._PulseMin); // Remap(sin, -1, 1, min, max)
    outColor = clamp(tex * u._Color * u._Color2 * pulse, 0.0, 1.0);
}
