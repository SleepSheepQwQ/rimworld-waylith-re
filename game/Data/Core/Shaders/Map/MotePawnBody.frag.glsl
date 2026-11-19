#version 450

// Shared by MotePawnBodyColor (alpha blend) and MotePawnBodyGlow (additive)
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec4 color = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy);
    float outline = min(step(0.33, color.r) + 0.5, 1.0);
    vec4 finalValue = u._Color;
    finalValue.a *= color.a * outline;
    outColor = clamp(finalValue, 0.0, 1.0);
}
