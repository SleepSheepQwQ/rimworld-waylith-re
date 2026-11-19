#version 450

layout(location = 0) in vec4 vColor;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _CastVect; // global, map sun light direction (SkyManager)
} u;

void main()
{
    outColor = vColor;
}
