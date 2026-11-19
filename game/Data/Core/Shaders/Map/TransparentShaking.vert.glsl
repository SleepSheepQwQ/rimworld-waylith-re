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
    vec4 _Amplitude;        // @default = vec4(1.0, 1.0, 0.0, 0.0)
    float _Speed;           // @default = 1.0
    float _AgeSecs;         // @default = 0.0
    float _RandomPerObject; // @default = 0.0
} u;

void main()
{
    float t = u._AgeSecs * u._Speed + u._RandomPerObject;
    vec4 offset = vec4(sin(t), 0.0, cos(t), 0.0) * u._Amplitude - u._Amplitude * 0.5;
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition + offset.xyz, 1.0);
    vColor = inColor;
    vUv = inUv0.xy;
}
