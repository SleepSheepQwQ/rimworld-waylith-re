#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) flat out float vFallSeed; // per plant seed from uv z

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;
    vec4 _MainTex_ST;
    float _SwayHead;
} uMaterial;

void main()
{
    const float Freq = 0.15;
    const float Amp = 0.1;
    float head = uMaterial._SwayHead;

    vec3 windOffset = vec3(sin(head * Freq + inPosition.x * 4.0), 0.0, sin(head * Freq + inPosition.z * 4.0)) * Amp;
    vec3 pos = inPosition + windOffset * inColor.a;

    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(pos, 1.0);
    vColor = inColor;
    vUv = inUv0.xy;
    vFallSeed = inUv0.z;
}
