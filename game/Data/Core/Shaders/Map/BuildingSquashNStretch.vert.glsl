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
    vec4 _SquashNStretch;   // @default = vec4(1.0, 1.0, 0.0, 0.0)
    float _AgeSecs;         // @default = 0.0
    float _RandomPerObject; // @default = 0.0
} u;

float remap(float value, float from1, float to1, float from2, float to2)
{
    return (value - from1) / (to1 - from1) * (to2 - from2) + from2;
}

vec4 squash(float sq)
{
    float stretch = 1.0 - sq;
    vec2 minMax = u._SquashNStretch.rg;
    float x = remap(sq, -1.0, 1.0, minMax.x, minMax.y);
    float y = remap(stretch, -1.0, 1.0, minMax.x, minMax.y);
    return vec4(x, y, u._SquashNStretch.b - 0.5 * x, u._SquashNStretch.a + 0.5 * y);
}

void main()
{
    vec4 st = squash(sin((u._AgeSecs + mod(u._RandomPerObject, 10000.0)) * 1.5));
    vUv = inUv0.xy * st.xy + st.zw;
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vColor = inColor;
}
