#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _texBScrollSpeed; // @default = 0.5
    float _texBScale;       // @default = 1.0
    float _Intensity;       // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MultiplyTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = (length(centeredOffset) - 0.15) * 1.25;

    float angle = atan(centeredOffset.x, centeredOffset.y);

    float texA = texture(_MainTex, flipV(vUv)).r;
    float texB = texture(_MultiplyTex, flipV(vec2(angle * u._texBScale, distToCenter + u._AgeSecs * u._texBScrollSpeed))).r;

    float innerFade = 1.0 - pow(1.0 - distToCenter, 3.0);

    outColor = clamp(u._Color * texA * texB * u._Intensity * innerFade, 0.0, 1.0);
}
