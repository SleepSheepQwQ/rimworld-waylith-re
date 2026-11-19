#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _Rotation; // @default = 0.0 (0..3 quadrant of a 4-frame strip)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    float uvx = 0.25 * u._Rotation + vUv.x * 0.25;
    vec4 tex = texture(_MainTex, vec2(uvx, 1.0 - vUv.y)); // V flip
    outColor = clamp(tex * u._Color, 0.0, 1.0);
}
