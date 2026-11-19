#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;       // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _Working;         // @default = 0.0
    float _RandomPerObject; // @default = 0.0
} u;

void main()
{
    const float shakeInterval = 5.0;
    const float shakeIntensity = 0.007;
    const float shakeTime = 2.0;
    float uniqueAge = u._AgeSecs + mod(abs(u._RandomPerObject), 100000.0);
    float intervald = mod(uniqueAge, shakeInterval);
    float shake = 1.0 - clamp(intervald / shakeTime, 0.0, 1.0);
    vec3 shakeOffset = vec3(sin(uniqueAge * 53.0), 0.0, cos(uniqueAge * 59.0)) * shakeIntensity * shake * u._Working;
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition + shakeOffset, 1.0);
    vColor = inColor;
    vUv = inUv0.xy;
}
