#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vObjectWorld;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _WorldSpaceCameraPos;       // global, MapRenderFeature
    float _AgeSecs;                  // @default = 0.0
    float _DestructionStartAgeSecs;  // @default = 999999.0
    float _AmbientShake;             // @default = 0.0
    float _Parallax;                 // @default = 1.0
} u;

void main()
{
    float destructionAlpha = clamp((u._AgeSecs - u._DestructionStartAgeSecs) / 11.0, 0.0, 1.0);
    vec3 offset = vec3(sin(u._AgeSecs * 70.0), cos(u._AgeSecs * 20.0), 0.0) * 0.42 * (destructionAlpha + u._AmbientShake);
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition + offset, 1.0);
    vObjectWorld = (push.modelMatrix * vec4(0.0, 0.0, 0.0, 1.0)).xz;
    vUv = inUv0.xy;
}
