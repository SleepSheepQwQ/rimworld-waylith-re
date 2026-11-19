#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DistortionTex;
layout(set = 2, binding = 3) uniform sampler2D _GrabVoidNode;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;

    float distToCenter = length(vUv);

    float angle = atan(vUv.x, vUv.y);
    vec4 distortion = texture(_DistortionTex, flipV(vec2(angle, distToCenter + u._AgeSecs * 0.25)));

    float outsideCutout = 1.0 - clamp(length(vUv * 1.9), 0.0, 1.0);
    float centerCutout = clamp(length(vUv * 10.0) - 0.5, 0.0, 1.0);
    float mask = outsideCutout * centerCutout;

    float distortionValue = distortion.w * 0.12;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabVoidNode, 0));
    vec2 offset = centeredOffset * distortionValue;
    outColor = texture(_GrabVoidNode, grabUv - vec2(offset.x, -offset.y)) * mask;
}
