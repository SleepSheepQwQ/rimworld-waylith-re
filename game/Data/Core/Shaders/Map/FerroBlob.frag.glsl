#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;           // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;      // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _Color2;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _NoiseReadScale; // @default = 0.008
    float _AgeSecs;        // @default = 0.0
    float _RampTime;       // @default = 0.2
    float _NoiseScale;     // @default = 0.5
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _RippleTex; // @vertex

void main()
{
    outColor = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy) * u._Color * u._Color2;
}
