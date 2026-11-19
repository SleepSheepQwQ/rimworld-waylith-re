#version 450

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0) - sun shadow colour
} uMaterial;

void main()
{
    outColor = uMaterial._Color;
}
