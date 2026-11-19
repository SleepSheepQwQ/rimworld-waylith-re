#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _Noise;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);

    float distortion = sqrt(abs(1.0 - dot(centeredOffset, centeredOffset)));
    float extraBrightness = 1.0 + pow(abs(distToCenter), 20.0) * 5.0;

    vec4 color = vec4(1.0);
    color.rg *= texture(_Noise, flipV(centeredOffset / distortion * 0.5 + vec2(u._AgeSecs * 0.3, 0.3))).rg * extraBrightness;
    color.a = texture(_MainTex, flipV(vUv)).r * u._Color.a;

    outColor = color;
}
