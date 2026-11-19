#version 450

#ifdef INSTANCED
#include "InstanceTransforms.glsl"
#include "InstanceVacuumPercents.glsl"
#endif

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vWorldXZ;
layout(location = 3) out float vVacuumPercent;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
#ifdef INSTANCED
    vec4 world = instanceTransforms[gl_InstanceIndex] * vec4(inPosition, 1.0);
    vVacuumPercent = instanceVacuumPercents[gl_InstanceIndex];
#else
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    vVacuumPercent = 1.0;
#endif
    gl_Position = uVertex.ViewProj * world;

    vColor = vec4(1.0);
    vUv = inUv0.xy;
    vWorldXZ = world.xz;
}
