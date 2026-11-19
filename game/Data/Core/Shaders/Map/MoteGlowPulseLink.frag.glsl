#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in float vScale;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;     // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _Duration;        // @default = 0.3
    float _FalloffExponent; // @default = 3.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    float progress = u._AgeSecs / u._Duration; // fade runs along the raw mesh V
    float dist = pow(1.0 - clamp(abs(vUv.y - progress) * vScale, 0.0, 1.0), u._FalloffExponent);
    vec4 tex = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy);
    tex.a = dist;
    outColor = clamp(tex * u._Color, 0.0, 1.0);
}
