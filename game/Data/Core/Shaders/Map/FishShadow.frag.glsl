#version 450

#ifdef INSTANCED
#include "InstanceTransforms.glsl"
#include "InstanceColors.glsl"
#endif

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec4 vInstColor;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;     // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _Time;           // global, MapRenderFeature
    float _SwimIntensity; // @default = 0.05
    float _SwimFrequency; // @default = 10.0
    float _SwimSpeed;     // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
#ifdef INSTANCED
    vec4 color = vInstColor;
#else
    vec4 color = u._Color;
#endif
    vec2 texCoord = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec2 uv = 0.5 - texCoord;
    float angle = atan(uv.y, uv.x) + u._Time.y * -u._SwimSpeed;
    float offset = sin(angle * u._SwimFrequency);
    float intensity = u._SwimIntensity * length(uv);
    outColor = texture(_MainTex, flipV(texCoord + normalize(uv) * offset * intensity)) * color * vColor;
}
