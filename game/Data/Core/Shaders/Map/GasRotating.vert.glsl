#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vDensityAlphas;
layout(location = 1) out vec3 vUv;
layout(location = 2) out float vAlpha;
layout(location = 3) out vec2 vWorldPos;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;
    vUv = inUv0.xyz;
    float total = inColor.x + inColor.y + inColor.z + inColor.w;
    vDensityAlphas = inColor / total;
    vAlpha = mix(0.2, 1.0, clamp(total / 1020.0, 0.0, 1.0));
    vWorldPos = world.xz;
}
