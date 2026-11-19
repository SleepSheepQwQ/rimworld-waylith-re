#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Color2; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;                     // @default = 0.0
    float _texAScrollSpeed;             // @default = 0.5
    float _texBScrollSpeed;             // @default = 0.5
    float _texAScale;                   // @default = 1.0
    float _texBScale;                   // @default = 1.0
    float _Intensity;                   // @default = 1.0
    float _DistortionIntensity;         // @default = 1.0
    float _DistortionSpeed;             // @default = 1.0
    float _InnerFadeAmount;             // @default = 1.0
    float _TexAScaleDivisorPerProgress; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DistortionTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _MultiplyTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = (length(centeredOffset) - 0.35) * 1.45;

    if (distToCenter > 1.0)
        discard;

    float angle = atan(centeredOffset.x, centeredOffset.y);

    float distortionTex = texture(_DistortionTex, flipV(vec2(angle * u._texBScale, clamp(distToCenter, 0.0, 1.0) + u._AgeSecs * u._DistortionSpeed))).r * u._DistortionIntensity;
    vec4 texA = texture(_MainTex, flipV(vec2(angle * u._texAScale, clamp(distToCenter, 0.0, 1.0) * (1.0 + u._TexAScaleDivisorPerProgress * u._AgeSecs) + u._AgeSecs * u._texAScrollSpeed + distortionTex)));

    float texB = texture(_MultiplyTex, flipV(vec2(angle * u._texBScale, clamp(distToCenter, 0.0, 1.0) + u._AgeSecs * u._texBScrollSpeed))).r;
    float innerFade = 1.0 - pow(1.0 - distToCenter, 3.0) * u._InnerFadeAmount;
    vec4 col = u._Color * u._Color2 * texA * texB * u._Intensity * innerFade;
    col.a = u._Color.a * u._Color2.a * texA.a;
    outColor = clamp(col, 0.0, 1.0);
}
