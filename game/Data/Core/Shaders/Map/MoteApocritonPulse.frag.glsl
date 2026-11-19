#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _distortionTint;       // @default = vec4(0.0, 0.0, 0.0, 0.0)
    float _distortionIntensity; // @default = 0.05
    float _pulseSpeed;          // @default = 1.0
    float _ScrollSpeed;         // @default = 0.5
    float _ScrollScale;         // @default = 0.5
    float _WaveOpacity;         // @default = 0.2
    float _AgeSecs;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _PulseTex;
layout(set = 2, binding = 3) uniform sampler2D _GrabDistortion;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec2 rotateUV(vec2 uv, float rotation)
{
    const float mid = 0.5;
    return vec2(
        cos(rotation) * (uv.x - mid) + sin(rotation) * (uv.y - mid) + mid,
        cos(rotation) * (uv.y - mid) - sin(rotation) * (uv.x - mid) + mid);
}

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);
    float sinVal = (u._AgeSecs * 0.1 * u._pulseSpeed + distToCenter) * 3.0;
    vec2 rotatedUv = rotateUV(vUv, sinVal * u._Color.a);

    if (distToCenter > 1.0)
        discard;

    float distortion = texture(_MainTex, flipV(rotatedUv)).r;
    vec2 offset = sin(centeredOffset * pow(distortion * u._distortionIntensity, 2.0));

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec4 bgcolor = texture(_GrabDistortion, grabUv + vec2(offset.x, -offset.y));

    float angle = atan(centeredOffset.x, centeredOffset.y);
    float distDivisor = 1.0 + distToCenter * 2.0;
    vec4 tex = texture(_PulseTex, flipV(vec2(angle / distDivisor + (gl_FragCoord.y + gl_FragCoord.x) / 1000.0, (distToCenter + u._AgeSecs * u._ScrollSpeed * 0.5 + 0.5) * u._ScrollScale)));

    bgcolor = mix(bgcolor, bgcolor + u._distortionTint, pow(abs(distortion), 1.8) * u._Color.a);

    outColor = bgcolor + (tex.r * u._distortionTint * u._WaveOpacity) * u._Color.a;
}
