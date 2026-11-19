#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _texAScrollSpeed; // @default = 0.5
    float _texBScrollSpeed; // @default = 0.5
    float _texAScale;       // @default = 1.0
    float _texBScale;       // @default = 1.0
    float _Intensity;       // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MultiplyTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _FadeTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = (length(centeredOffset) - 0.35) * 1.45;

    float angle = atan(centeredOffset.x, centeredOffset.y);

    float texA = texture(_MainTex, flipV(vec2(angle * u._texAScale, clamp(distToCenter, 0.0, 1.0) + u._AgeSecs * u._texAScrollSpeed))).r;
    float texB = texture(_MultiplyTex, flipV(vec2(angle * u._texBScale, clamp(distToCenter, 0.0, 1.0) + u._AgeSecs * u._texBScrollSpeed))).r;

    vec4 result = u._Color;
    result.a *= u._Intensity * texA * texB * texture(_FadeTex, flipV(vUv)).r;
    outColor = result;
}
