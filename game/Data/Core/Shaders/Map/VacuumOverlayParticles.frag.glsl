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
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _TuningColor;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MaskTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _MainTexColor;  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex2_ST;   // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _MainTex2Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _BobFreqAmp;    // @color @default = vec4(1.0, 1.0, 0.0, 0.0)
    float _MainTexSpeed;  // @default = 1.0
    float _MainTex2Speed; // @default = 1.0
    float _GameTime;      // global, set by GlobalRendererUtility
} u;

layout(set = 2, binding = 1) uniform sampler2D _MaskTex;
layout(set = 2, binding = 4) uniform sampler2D _MainTex;
layout(set = 2, binding = 5) uniform sampler2D _MainTex2;
layout(set = 2, binding = 6) uniform sampler2D _NoiseTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 baseUv = vWorldXZ / 8.0;
    vec2 uv1 = baseUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec2 uv2 = baseUv * u._MainTex2_ST.zw + u._MainTex2_ST.xy;
    vec2 uvMask = vUv * u._MaskTex_ST.zw + u._MaskTex_ST.xy;

    float mask = texture(_MaskTex, flipV(uvMask)).a;
    vec4 texCol1 = texture(_MainTex, flipV(uv1 + u._GameTime * u._MainTexSpeed));
    vec2 offset = vec2(u._GameTime * u._MainTex2Speed, sin(u._GameTime * u._BobFreqAmp.x) * u._BobFreqAmp.y);
    vec4 noise = texture(_NoiseTex, flipV(vec2(-1.0, 1.0) * uv1 + 0.34 + offset));
    vec4 texCol2 = texture(_MainTex2, flipV(uv2 + offset))
        * smoothstep(0.2, 0.6, noise.r)
        * smoothstep(0.95, 1.0, mask); // stops particles appearing inside walls

    outColor = max(texCol1 * u._MainTexColor, texCol2 * u._MainTex2Color)
        * u._Color
        * u._TuningColor
        * vVacuumPercent
        * mask
        * 2.0;
}
