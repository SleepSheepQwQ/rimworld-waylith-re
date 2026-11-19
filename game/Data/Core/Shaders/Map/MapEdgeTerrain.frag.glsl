#version 450

layout(location = 1) in vec2 vUv; // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Main_TexScale;  // @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Main_TexOffset; // @default = vec4(0.0, 0.0, 0.0, 0.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec2 uv = (vUv * uMaterial._Main_TexScale.xz + uMaterial._Main_TexOffset.xz) / 16.0;
    uv.y = -uv.y; 
    outColor = texture(_MainTex, uv) * uMaterial._Color;
}
