#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;        // @default = 0.0
    float _Intensity;      // @default = 1.0
    float _FramesPerSec;   // @default = 1.0
    float _NumFrames;      // @default = 1.0
    float _RandomPerObjectOffsetRandom; // global @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _GradientTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _AgeScalarNoise; // @vertex

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 tex = texture(_MainTex, flipV(vUv));
    vec2 gradientUv = vec2(u._AgeSecs * 1.5, 0.0);
    vec4 gradColor = texture(_GradientTex, flipV(gradientUv));

    float intensity = 0.0;
    vec4 f = (tex + intensity * tex.r) * u._Color * gradColor * (1.0 + sin(u._AgeSecs * 45.0) * 0.15);
    outColor = clamp(f, 0.0, 1.0);
}
