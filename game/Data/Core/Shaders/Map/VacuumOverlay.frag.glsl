#version 450

#ifdef INSTANCED
#include "InstanceTransforms.glsl"
#include "InstanceVacuumPercents.glsl"
#endif

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vWorldXZ;
layout(location = 3) in float vVacuumPercent;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;       // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MaskTex_ST;  // @default = vec4(0.0, 0.0, 1.0, 1.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MaskTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    const vec4 WHITE = vec4(1.0, 1.0, 1.0, 0.0);

    vec2 maskUv = vUv * u._MaskTex_ST.zw + u._MaskTex_ST.xy;
    vec4 col = u._Color;
    col = mix(WHITE, col, smoothstep(0.0, 0.2, clamp((vVacuumPercent - 0.5) * 2.0, 0.0, 1.0)));
    col = mix(WHITE, col, texture(_MaskTex, flipV(maskUv)).a);
    outColor = col;
}
