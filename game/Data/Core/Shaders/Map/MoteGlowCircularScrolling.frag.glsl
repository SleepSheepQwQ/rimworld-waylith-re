#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;             // @default = 0.0
    float _ScrollSpeed;         // @default = 0.5
    float _ScrollScale;         // @default = 0.5
    float _DistortionIntensity; // @default = 0.5
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;
layout(set = 2, binding = 3) uniform sampler2D _DistortionTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float opacity = texture(_MaskTex, flipV(vUv)).r;
    float distortion = texture(_DistortionTex, flipV(vUv)).r;
    vec4 centeredOffset = vec4(vUv * 2.0 - 1.0, 0.0, 0.0);
    float distToCenter = (length(centeredOffset) - 0.35 + distortion * u._DistortionIntensity) * 1.45;

    if (distToCenter > 1.0)
        discard;

    float angle = atan(centeredOffset.x, centeredOffset.y);
    float distDivisor = 1.0 + distToCenter * 2.0;
    vec4 tex = texture(_MainTex, flipV(vec2(angle / distDivisor + u._AgeSecs * u._ScrollSpeed + (gl_FragCoord.y + gl_FragCoord.x) / 1000.0, (distToCenter + u._AgeSecs * u._ScrollSpeed * 0.5 + 0.5) * u._ScrollScale)));
    vec4 f = vec4(u._Color.r, u._Color.g, u._Color.b, tex.r * u._Color.a * 1.5 * opacity);
    outColor = clamp(f, 0.0, 1.0);
}
