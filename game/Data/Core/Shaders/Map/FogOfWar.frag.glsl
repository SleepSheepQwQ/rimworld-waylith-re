#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv; // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DetailTex;

void main()
{
    vec2 uv = vUv / 64.0;
    uv.y = -uv.y; 
    vec4 mainCol = texture(_MainTex, uv);
    vec4 detailCol = texture(_DetailTex, uv * 8.0);
    outColor = mainCol * detailCol * vColor * uMaterial._Color;
}
