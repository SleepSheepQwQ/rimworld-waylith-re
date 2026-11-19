#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;
    float _brightnessMultiplier; // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _GrabDistortion;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float distortion = texture(_MainTex, flipV(vUv)).r * 0.0015 * u._Color.a;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec4 bgcolor = texture(_GrabDistortion, grabUv + vec2(sin(u._AgeSecs * 3.0) * distortion, 0.0));
    outColor = mix(bgcolor, bgcolor * u._brightnessMultiplier, u._Color.a * distortion);
}
