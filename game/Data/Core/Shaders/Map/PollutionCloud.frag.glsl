#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;      // sprite UV
layout(location = 2) in vec2 vWorldXZ; // world position (identity model for section meshes)

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;              // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;         // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _FadeTexScale;       // @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _FadeTexScrollSpeed; // @default = vec4(0.0, 0.0, 0.0, 0.0)
    float _GameSeconds;       // global, set by TickManager
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _FadeTex;

void main()
{
    vec4 col = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy);
    col.a *= texture(_FadeTex, vWorldXZ / 10.0 * u._FadeTexScale.xy + u._FadeTexScrollSpeed.xy * u._GameSeconds).r;
    outColor = col * vColor * u._Color;
}
