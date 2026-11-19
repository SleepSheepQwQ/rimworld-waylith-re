#version 450

#ifdef INSTANCED
#include "InstanceTransforms.glsl"
#include "InstanceOxygenPercents.glsl"
#endif

layout(location = 0) in vec3 inPosition; // @vertex Position required

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 2) out vec2 vWorldXZ;
layout(location = 3) out float vOxygenPercent;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
#ifdef INSTANCED
    vec4 world = instanceTransforms[gl_InstanceIndex] * vec4(inPosition, 1.0);
    vOxygenPercent = instanceOxygenPercents[gl_InstanceIndex];
#else
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    vOxygenPercent = 1.0;
#endif
    gl_Position = uVertex.ViewProj * world;
    vWorldXZ = world.xz;
}
