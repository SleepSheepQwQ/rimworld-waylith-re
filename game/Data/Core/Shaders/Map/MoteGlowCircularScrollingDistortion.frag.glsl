#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _ScrollScale;           // @default = vec4(0.5, 0.5, 0.0, 0.0)
    vec4 _DistortionTint;        // @default = vec4(0.0, 0.0, 0.0, 0.0)
    float _AgeSecs;
    float _ScrollSpeed;          // @default = 0.5
    float _DistortionIntensity;  // @default = 0.5
    float _BrightnessMultiplier; // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;
layout(set = 2, binding = 3) uniform sampler2D _GrabDistortionBackground;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float opacity = texture(_MaskTex, flipV(vUv)).r;

    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);
    float angle = atan(centeredOffset.x, centeredOffset.y);
    float sinVal = (u._AgeSecs * 0.1 * u._ScrollSpeed + distToCenter) * 3.0;

    float distortion = texture(_MainTex, flipV(vec2(angle * u._ScrollScale.x, sinVal * u._ScrollScale.y))).r * opacity * u._DistortionIntensity;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortionBackground, 0));
    vec2 offset = sin(centeredOffset * distortion);
    vec4 bgcolor = texture(_GrabDistortionBackground, grabUv + vec2(offset.x, -offset.y));
    outColor = mix(bgcolor, bgcolor * u._BrightnessMultiplier + u._DistortionTint, distortion * 8.0);
}
