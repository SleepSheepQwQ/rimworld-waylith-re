#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 1) out vec2 vUv;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _AgeSecs; // @default = 0.0
} u;

void main()
{
    float dissolveAlpha = clamp(u._AgeSecs / 2.0, 0.0, 1.0);
    vec4 offset = vec4(sin(u._AgeSecs * 70.0), cos(u._AgeSecs * 50.0), 0.0, 0.0) * 0.004 * dissolveAlpha;
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0) + offset;
    vUv = inUv0.xy;
}
