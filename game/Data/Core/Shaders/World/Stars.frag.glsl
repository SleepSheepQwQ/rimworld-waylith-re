#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    // TODO WAYLITH: Force mip 0 (textureLod). BC7 mode-6 compression breaking transparency here. Remove after that.
    outColor = textureLod(_MainTex, vUv * uMaterial._MainTex_ST.zw + uMaterial._MainTex_ST.xy, 0.0);
}
