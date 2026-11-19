#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _SquashNStretch;   // @default = vec4(1.0, 1.0, 0.0, 0.0)
    float _AgeSecs;         // @default = 0.0
    float _RandomPerObject; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 f = texture(_MainTex, flipV(vUv)) * u._Color * vColor;
    if (f.a < 0.5)
        discard;
    outColor = f;
}
