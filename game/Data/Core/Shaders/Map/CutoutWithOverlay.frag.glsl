#version 450

#include "MaskGate.glsl"

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;           // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;      // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _OverlayColor;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _OverlayOpacity; // @default = 0.0
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;

void main()
{
    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec4 mask = texture(_MaskTex, uv);
    float overlay = mask.r * MaskGate(mask);
    vec4 f = mix(texture(_MainTex, uv), uMaterial._OverlayColor, uMaterial._OverlayOpacity * overlay) * uMaterial._Color * vColor;
    if (f.a < 0.5)
        discard;
    outColor = f;
}
