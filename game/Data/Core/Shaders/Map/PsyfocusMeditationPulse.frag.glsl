#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _distortionIntensity;  // @default = 0.05
    float _brightnessMultiplier; // @default = 1.0
    float _pulseSpeed;           // @default = 1.0
    float _AgeSecs;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _GrabDistortion;

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);

    if (distToCenter > 1.0)
        discard;

    float sinVal = sin(u._AgeSecs * u._pulseSpeed);
    float distortion = sinVal * pow(max(1.0 - distToCenter, 0.001), 0.5) * abs(distToCenter - 0.25) * 2.0;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec2 offset = centeredOffset * distortion * u._distortionIntensity;
    vec4 bgcolor = texture(_GrabDistortion, grabUv - vec2(offset.x, -offset.y));
    outColor = mix(bgcolor, bgcolor * u._brightnessMultiplier, abs(distortion));
}
