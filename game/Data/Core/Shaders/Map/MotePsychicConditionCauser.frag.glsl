#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _distortionIntensity;  // @default = 0.05
    float _distortionScale;      // @default = 1.0
    float _brightnessMultiplier; // @default = 1.0
    float _AgeSecs;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _GrabDistortion;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 tex = texture(_MainTex, flipV(vUv));
    float opacity = u._Color.a * tex.r;

    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);

    float distortion = sin(distToCenter * u._distortionScale + u._AgeSecs * 1.5) * u._distortionIntensity * opacity;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec2 offset = centeredOffset * distortion;
    vec4 bgcolor = texture(_GrabDistortion, grabUv - vec2(offset.x, -offset.y));
    outColor = mix(bgcolor, bgcolor * u._brightnessMultiplier, opacity);
}
