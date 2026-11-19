#version 450

layout(location = 4) in vec3 vPosLocal;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _TuningColor;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTexColor;  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex2Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _MainTex2_ST;   // @default = vec4(0.0, 0.0, 1.0, 1.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MainTex2;

void main()
{
    vec2 uv = vPosLocal.xz / 8.0;
    vec4 texCol1 = texture(_MainTex, uv * u._MainTex_ST.zw + u._MainTex_ST.xy);
    vec4 texCol2 = texture(_MainTex2, uv * u._MainTex2_ST.zw + u._MainTex2_ST.xy);
    outColor = (texCol1 * u._MainTexColor + texCol2 * u._MainTex2Color) * u._Color * u._TuningColor * 2.0;
}
