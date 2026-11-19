#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vScale;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _distortionScrollSpeed;    // @default = vec4(0.15, 0.15, 0.0, 0.0)
    vec4 _textureRepeatAmount;      // @default = vec4(0.0, 0.0, 0.0, 0.0) (0 = no repeat)
    float _distortionScale;           // @default = 0.2
    float _distortionIntensity;       // @default = 0.3
    float _wordSpaceDistortionToggle; // @default = 0.0
    float _GameSeconds;               // global, set by TickManager
    float _RandomPerObject;           // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DistortionTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 scale = vec2(u._distortionScale);
    if (u._wordSpaceDistortionToggle > 0.1)
        scale *= vScale;

    vec4 distortion = texture(_DistortionTex, flipV(vUv * scale + u._RandomPerObject + u._GameSeconds * u._distortionScrollSpeed.xy)) * u._distortionIntensity - u._distortionIntensity * 0.5;

    vec2 texCoord = vUv;
    if (u._textureRepeatAmount.x > 0.0)
        texCoord.x = vScale.x * u._textureRepeatAmount.x * texCoord.x;
    if (u._textureRepeatAmount.y > 0.0)
        texCoord.y = vScale.y * u._textureRepeatAmount.y * texCoord.y;

    vec4 tex = texture(_MainTex, flipV(texCoord + distortion.xy));
    outColor = clamp(tex * u._Color, 0.0, 1.0);
}
