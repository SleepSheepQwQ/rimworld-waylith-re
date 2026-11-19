#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor;    // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _CastVect; // global, map sun light direction (SkyManager)
} u;

void main()
{
    vColor = mix(u._Color, vec4(1.0), inColor.r);
    vec4 worldPos = push.modelMatrix * vec4(inPosition, 1.0);
    worldPos.xyz += u._CastVect.xyz * inColor.a;
    gl_Position = uVertex.ViewProj * worldPos;
}
