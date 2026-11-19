#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _texAScrollSpeed; // @default = 0.5
    float _texBScrollSpeed; // @default = 0.5
    float _texScale;        // @default = 1.0
    float _Intensity;       // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _ExtraTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 centeredOffset = vec4(vUv * 2.0 - 1.0, 0.0, 0.0);
    float distToCenter = length(centeredOffset) - 0.12; // small offset to avoid corner artifacts

    float angle = atan(centeredOffset.x, centeredOffset.y);
    float scale = u._texScale + u._AgeSecs * 0.1;
    vec4 texA = texture(_MainTex, flipV(vec2(angle * scale + u._AgeSecs * u._texAScrollSpeed, distToCenter - u._AgeSecs * 0.125)));
    vec4 texB = texture(_ExtraTex, flipV(vec2(angle * scale + u._AgeSecs * u._texBScrollSpeed, distToCenter - u._AgeSecs * 0.125)));

    outColor = clamp(u._Color * max(texA, texB) * u._Intensity, 0.0, 1.0);
}
