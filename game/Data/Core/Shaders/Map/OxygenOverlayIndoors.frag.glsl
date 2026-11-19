#version 450

#ifdef INSTANCED
#include "InstanceTransforms.glsl"
#include "InstanceOxygenPercents.glsl"
#endif

layout(location = 2) in vec2 vWorldXZ;
layout(location = 3) in float vOxygenPercent;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _TuningColor;           // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Tex_ST;                // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _NoiseTex_ST;           // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec2 _MaskFrequency;         // @default = vec2(1.0, 1.0)
    float _NoiseIntensity;       // @default = 1.0
    float _NoiseIntensityForMask; // @default = 1.0
    float _MaskLowerThreshold;   // @default = 0.4
    float _MaskUpperThreshold;   // @default = 0.8
    float _MovementSpeed;        // @default = 1.0
    float _GameTime;             // global, set by GlobalRendererUtility
} u;

layout(set = 2, binding = 1) uniform sampler2D _Tex;
layout(set = 2, binding = 4) uniform sampler2D _NoiseTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float time = u._GameTime / 20.0 * u._MovementSpeed;
    vec2 uv = vWorldXZ / 8.0;
    vec2 uv1 = uv * u._Tex_ST.zw + u._Tex_ST.xy;
    vec2 uv2 = uv * u._NoiseTex_ST.zw + u._NoiseTex_ST.xy;
    vec4 noise = texture(_NoiseTex, flipV(uv2 + vec2(0.0, time * 0.25)));
    vec4 mask = texture(_NoiseTex, flipV((uv2 - vec2(0.0, time * 0.25) + noise.xy * u._NoiseIntensityForMask) * u._MaskFrequency));
    vec4 texCol1 = texture(_Tex, flipV(uv1 - vec2(0.0, time * 0.5) + noise.xy * u._NoiseIntensity));
    texCol1.a = clamp(vOxygenPercent * 2.0, 0.0, 1.0) * 2.0 * smoothstep(u._MaskLowerThreshold, u._MaskUpperThreshold, mask.r);
    outColor = texCol1 * u._TuningColor * u._Color;
}
