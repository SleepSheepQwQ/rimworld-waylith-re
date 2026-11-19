#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vScale;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _ScrollSpeedA;   // @default = 0.1
    float _ScrollSpeedB;   // @default = 0.1
    float _AgeSecs;        // @default = 0.0
    float _Intensity;      // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _ExtraTexA; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _ExtraTexB; // @default = black

// Textures are stored V-inverted vs Unity; flip V on the final (computed) sample coordinate.
vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 texCoordCorrected = vUv;
    float scaledSegment = 1.0 / vScale.y;
    float yScaled = texCoordCorrected.y * vScale.y;
    float yFrac = fract((1.0 - texCoordCorrected.y) * vScale.y);

    if (vUv.y < scaledSegment * 0.5)
        texCoordCorrected.y = yScaled;
    else if (vUv.y >= 1.0 - scaledSegment * 0.5)
        texCoordCorrected.y = 1.0 - yFrac * 0.666;
    else
    {
        texCoordCorrected.y *= 0.333;
        texCoordCorrected.y += 0.333;
    }

    vec4 tex = texture(_MainTex, flipV(texCoordCorrected));
    vec4 extraB = texture(_ExtraTexB, flipV(vUv * vec2(1.0, vScale.y / 5.0) + vec2(0.0, u._ScrollSpeedB * u._AgeSecs)));
    vec4 extraA = texture(_ExtraTexA, flipV(vUv * vec2(0.66 + extraB.r * 0.1 - 0.05, vScale.y / 5.0) + vec2(0.0, u._ScrollSpeedA * u._AgeSecs)));

    vec4 f = (tex + extraA * u._Intensity * tex.r) * u._Color * (1.0 + sin(u._AgeSecs * 45.0) * 0.15);
    outColor = clamp(f, 0.0, 1.0);
}
