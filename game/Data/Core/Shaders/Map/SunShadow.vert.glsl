#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor;    // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0) - .a = cast distance

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

// FragmentUniforms is bound to the vertex stage too (see MeshBatchRenderer); we only need SunDir here.
layout(set = 0, binding = 1) uniform FragmentUniforms
{
    vec4 SunDir; // .xyz = sun shadow cast vector (world space)
    mat4 WorldToCamera;
    mat4 ViewProj;
} uFragment;

void main()
{
    vec4 worldPos = push.modelMatrix * vec4(inPosition, 1.0);
    worldPos.xyz += uFragment.SunDir.xyz * inColor.a;
    gl_Position = uVertex.ViewProj * worldPos;
}
