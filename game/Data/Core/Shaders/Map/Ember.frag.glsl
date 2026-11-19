#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Color2;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    outColor = texture(_MainTex, flipV(vUv)) * u._Color * u._Color2;
}
