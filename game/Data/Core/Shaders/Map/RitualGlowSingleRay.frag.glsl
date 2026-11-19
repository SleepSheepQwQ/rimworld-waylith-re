#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;        // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;     // @default = 0.0
    float _ScrollSpeed; // @default = 0.1
    float _Intensity;   // @default = 10.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _ExtraTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 mainTex = texture(_MainTex, flipV(vUv));
    vec2 texCoord = vUv * vec2(0.5, 1.0);
    vec4 tex1 = texture(_ExtraTex, flipV(texCoord + vec2(u._AgeSecs * u._ScrollSpeed - 0.8, 0.0)));
    vec4 tex2 = texture(_ExtraTex, flipV(texCoord - vec2(u._AgeSecs * u._ScrollSpeed * 0.8 + 0.4, 0.0)));
    vec4 f = mainTex * max(tex1 + tex2, 0.15) * u._Color * u._Intensity;
    outColor = clamp(f, 0.0, 1.0);
}
