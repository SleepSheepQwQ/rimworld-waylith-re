#version 450

layout(location = 2) in vec2 vTexCoordCur;
layout(location = 3) in vec2 vTexCoordNext;
layout(location = 4) in float vScale;
layout(location = 5) in float vFrameProgress;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;             // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _DistortionScrollSpeed;  // @default = vec4(0.45, 0.45, 0.0, 0.0)
    float _AgeSecs;                 // @default = 0.0
    float _NumFrames;               // @default = 1.0
    float _FramesPerSec;            // @default = 1.0
    float _Intensity;               // @default = 1.0
    float _DistortionScale;         // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DistortionTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float progress = u._AgeSecs / 8.0;

    vec2 distCoord = gl_FragCoord.xy / vScale * u._DistortionScale + u._AgeSecs * (u._DistortionScrollSpeed.xy + progress);
    float distortion = (texture(_DistortionTex, flipV(distCoord)).r * 0.005 - 0.0025) * mix(0.0, 1.0, pow(progress, 2.0));

    vec4 tex = mix(texture(_MainTex, flipV(vTexCoordCur + vec2(distortion, 0.0))),
                   texture(_MainTex, flipV(vTexCoordNext + vec2(distortion, 0.0))),
                   vFrameProgress);

    outColor = clamp(tex * 2.0 * u._Color, 0.0, 1.0);
}
