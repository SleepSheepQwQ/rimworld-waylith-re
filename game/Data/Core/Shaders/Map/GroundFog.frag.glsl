#version 450

layout(location = 4) in vec3 vPosLocal;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _RandomPerObject; // @default = 0.0
    float _ScrollDir;       // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec2 rotateUV(vec2 uv, float rotation)
{
    const float mid = 0.5;
    return vec2(
        cos(rotation) * (uv.x - mid) + sin(rotation) * (uv.y - mid) + mid,
        cos(rotation) * (uv.y - mid) - sin(rotation) * (uv.x - mid) + mid);
}

vec4 fogLayer(vec2 uv, float age, float rotationAmt)
{
    float time = age * 0.05;
    float distToCenter = length(uv);
    vec2 twistedUv = rotateUV(uv, distToCenter * sin(time) * rotationAmt);
    float angle = atan(twistedUv.x, twistedUv.y);
    return texture(_MainTex, flipV(vec2(angle, distToCenter + time * u._ScrollDir)));
}

void main()
{
    vec2 uv1 = vPosLocal.xz;
    vec2 uv2 = uv1 * 0.8;
    float uniqueAge = u._AgeSecs + mod(abs(u._RandomPerObject), 100000.0);

    const vec4 tint = vec4(0.5, 0.5, 0.6, 1.0);
    vec4 fog1 = fogLayer(uv1, uniqueAge, 1.0);
    vec4 fog2 = fogLayer(uv2, uniqueAge, -1.0);
    float centerCutout = clamp(length(uv1 * 10.0), 0.0, 1.0);
    float softEdges = clamp(0.5 - length(uv1), 0.0, 1.0);
    vec4 fogTex = max(fog1, fog2);
    vec4 finalColor = fogTex * centerCutout * u._Color * tint * 0.5;
    finalColor.a *= 3.0 * softEdges;
    outColor = finalColor;
}
