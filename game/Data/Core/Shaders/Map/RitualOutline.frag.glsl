#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;       // @default = 0.0
    float _ScrollSpeed;   // @default = 0.1
    float _GradientScale; // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _GradientTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec2 rotate(vec2 v, float angle, vec2 center)
{
    return vec2(
        cos(angle) * (v.x - center.x) + sin(angle) * (v.y - center.y) + center.x,
        cos(angle) * (v.y - center.y) - sin(angle) * (v.x - center.x) + center.y);
}

void main()
{
    float scroll = u._AgeSecs * u._ScrollSpeed;
    vec2 gradientUv = rotate(vUv * 0.5 * u._GradientScale + vec2(0.0, scroll), 0.27, vec2(0.5));
    float gradient = texture(_GradientTex, flipV(gradientUv)).r;
    float opacity = texture(_MainTex, flipV(vUv)).r;
    vec4 finalColor = vec4(u._Color.rgb * opacity, 1.0);
    finalColor.rgb *= u._Color.a * gradient;
    outColor = finalColor;
}
