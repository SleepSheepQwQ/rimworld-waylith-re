#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vGlobalCoord;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _pawnCenterWorld;       // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _pawnDrawSizeWorld;     // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _texAScrollSpeed;       // @default = vec4(0.5, 0.5, 0.0, 0.0)
    vec4 _texBScrollSpeed;       // @default = vec4(0.5, 0.5, 0.0, 0.0)
    vec4 _DetailScrollSpeed;     // @default = vec4(0.5, 0.5, 0.0, 0.0)
    vec4 _DistortionScrollSpeed; // @default = vec4(0.5, 0.5, 0.0, 0.0)
    vec4 _DetailOffset;          // @default = vec4(0.0, 0.0, 0.0, 0.0)
    float _AgeSecs;              // @default = 0.0
    float _detailIntensity;      // @default = 1.0
    float _texAScale;            // @default = 1.0
    float _texBScale;            // @default = 1.0
    float _DetailScale;          // @default = 1.0
    float _DetailDistortion;     // @default = 0.0
    float _OutlineMultiplier;    // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MultiplyTexA; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _MultiplyTexB; // @default = black
layout(set = 2, binding = 4) uniform sampler2D _DistortionTex; // @default = black
layout(set = 2, binding = 5) uniform sampler2D _DetailTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 color = texture(_MainTex, flipV(vUv));
    float alpha = color.a;
    float outline = step(0.33, color.r);
    float outlineDimmed = min(outline + 0.5, 1.0);

    float texA = texture(_MultiplyTexA, flipV(vGlobalCoord * u._texAScale + u._texAScrollSpeed.xy * u._AgeSecs)).r;
    float texB = texture(_MultiplyTexB, flipV(vGlobalCoord * u._texBScale + u._texBScrollSpeed.xy * u._AgeSecs)).r;
    float texDistortion = texture(_DistortionTex, flipV(vGlobalCoord * u._texBScale + u._DistortionScrollSpeed.xy * u._AgeSecs)).r;

    float detail = texture(_DetailTex, flipV(vGlobalCoord * u._DetailScale + u._DetailScrollSpeed.xy * u._AgeSecs + u._DetailOffset.xy + vec2(0.0, texDistortion * u._DetailDistortion))).r * u._detailIntensity;

    vec4 f = u._Color * (texA * texB + detail) * alpha * outlineDimmed + u._Color * max(u._OutlineMultiplier, 0.0) * (1.0 - outline) * alpha;
    outColor = clamp(f, 0.0, 1.0);
}
