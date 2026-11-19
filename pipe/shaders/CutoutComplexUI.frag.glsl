#version 450
// Faithful copy of Data/Core/Shaders/Map/CutoutComplexUI.frag.glsl as read from the device.
// The only edit is the `#include "MaskGate.glsl"` line, replaced by that file's two-line body
// (glslangValidator only accepts `#include` behind GL_GOOGLE_include_directive, which the shipped
// shader does not declare). The fragment body below is byte-for-byte the shipped one, including
// the absence of `if (f.a < 0.5) discard;` - that is the whole difference from CutoutComplex.

float MaskGate(vec4 mask)
{
    return clamp(mask.a * 255.0, 0.0, 1.0);
}

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _ColorTwo;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black

void main()
{
    vec2 uv = vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy;
    vec4 f = texture(_MainTex, uv);
    vec4 colPower = texture(_MaskTex, uv);
    colPower.rg *= MaskGate(colPower);

    vec4 colMultiplier = colPower.r * (uMaterial._Color * vColor) + (1.0 - colPower.r);
    f *= colMultiplier;

    vec4 colTwoMultiplier = colPower.g * uMaterial._ColorTwo + (1.0 - colPower.g);
    f *= colTwoMultiplier;

    outColor = f;
}
