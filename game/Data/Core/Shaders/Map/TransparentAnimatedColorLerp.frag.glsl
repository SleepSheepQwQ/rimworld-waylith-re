#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _NumFrames;    // @default = 1.0
    float _AgeSecs;      // @default = 0.0
    float _FramesPerSec; // @default = 1.0
    float _Loop;         // @default = 1.0
    float _Duration;     // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _ColorLerp;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 lerpd = texture(_ColorLerp, flipV(vec2(clamp(u._AgeSecs / u._Duration, 0.0, 0.99), 0.0)));
    outColor = texture(_MainTex, flipV(vUv)) * u._Color * vColor * lerpd;
}
