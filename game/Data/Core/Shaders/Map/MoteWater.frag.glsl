#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _ShockwaveColor; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _ShockwaveSpan; // @default = 1.0
} uMaterial;

void main()
{
    float positionOnCurve = (1.0 - length(vUv - vec2(0.5)) * 2.0) / uMaterial._ShockwaveSpan;
    if (positionOnCurve < 0.0 || positionOnCurve > 1.0)
        discard;
    outColor = vec4(sin(positionOnCurve * 6.28318530718) * uMaterial._ShockwaveColor.a, 0.0, 0.0, 0.0);
}
