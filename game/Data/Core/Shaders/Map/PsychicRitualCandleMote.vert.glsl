#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _FlameTex_ST;
    float _AgeSecs;
    float _Interval;
    float _RandomPerObject;
} u;

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    float wiggle = cos((u._AgeSecs * 10.0) + (u._RandomPerObject / 1000.0)) * 0.002 * inUv0.y;
    gl_Position += vec4(wiggle, -0.03, 0.0, 0.0);
    vColor = inColor;
    vUv = inUv0.xy;
}
