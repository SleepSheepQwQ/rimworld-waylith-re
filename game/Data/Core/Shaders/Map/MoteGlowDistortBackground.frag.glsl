#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Color2;                // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _distortionIntensity;  // @default = 0.04
    float _brightnessMultiplier; // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _DistortionTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _GrabDistortionBackground;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distortion = texture(_DistortionTex, flipV(vUv)).x * u._distortionIntensity;
    if (distortion - 0.001 < 0.0)
        discard;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortionBackground, 0));
    vec2 offset = centeredOffset * distortion * u._Color.a;
    vec4 bgcolor = texture(_GrabDistortionBackground, grabUv - vec2(offset.x, -offset.y));
    bgcolor = mix(bgcolor, bgcolor * u._brightnessMultiplier, u._Color.a * distortion);
    vec4 tex = texture(_MainTex, flipV(vUv));

    outColor = bgcolor + tex * u._Color * u._Color2;
}
