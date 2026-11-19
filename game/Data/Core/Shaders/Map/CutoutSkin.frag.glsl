#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;       // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;  // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _ShadowColor; // @color @default = vec4(1.0, 0.0, 0.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec4 src = texture(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy);
    if (src.a < 0.1)
        discard;

    vec4 dst = src * uMaterial._Color * vColor;

    float shadow = (1.0 - src.r) * src.r; // darkness of the magic-white source art, peaking at mid-grey
    shadow = min(shadow, 0.11);
    dst += uMaterial._ShadowColor * shadow;

    outColor = dst;
}
