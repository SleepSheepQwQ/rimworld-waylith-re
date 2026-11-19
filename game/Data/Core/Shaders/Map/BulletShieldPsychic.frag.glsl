#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Time;                 // global, published by MapRenderFeature
    float _PulseInterval;       // @default = 0.7
    float _DistortionScale;     // @default = 0.5
    float _DistortionIntensity; // @default = 0.2
    float _MinAlpha;            // @default = 0.25
    float _MaxAlpha;            // @default = 0.5
    float _GameSeconds;         // global, set by TickManager
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DistortionTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float distToCenter = pow(distance(vUv, vec2(0.5)) * 2.0, 2.0);
    if (distToCenter > 1.0)
        discard;
    vec2 distortion = texture(_DistortionTex, flipV(vUv / u._DistortionScale + u._Time.y * 0.1)).xy * u._DistortionIntensity;
    vec4 tex = texture(_MainTex, flipV(vec2(distToCenter - u._GameSeconds * 0.35, 0.0) + distortion));
    vec4 f = tex * u._Color * mix(u._MinAlpha, u._MaxAlpha, abs(sin(u._GameSeconds / u._PulseInterval))) * distToCenter;
    outColor = clamp(f, 0.0, 1.0);
}
