#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;       // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _Working;         // @default = 0.0
    float _RandomPerObject; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec4 f = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy) * u._Color * vColor;
    if (f.a < 0.5)
        discard;
    outColor = f;
}
