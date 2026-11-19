#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _Color2;     // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _UnmaskedContribution; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;

void main()
{
    vec2 uv = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    float mask = u._UnmaskedContribution + (1.0 - u._UnmaskedContribution) * texture(_MaskTex, uv).r;
    vec4 col = texture(_MainTex, uv);
    outColor = clamp(col * mask * u._Color * vColor * u._Color2, 0.0, 1.0);
}
