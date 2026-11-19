#version 450

layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _TuningColor;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTexColor;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex2Color;  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;     // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _MainTex2_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _MainTexSpeed;  // @default = 1.0
    float _MainTex2Speed; // @default = 1.0
    float _GameTime;      // global, set by GlobalRendererUtility
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MainTex2;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 uv = vWorldPos.xz / 8.0;
    vec2 uv1 = uv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec2 uv2 = uv * u._MainTex2_ST.zw + u._MainTex2_ST.xy;
    vec4 texCol1 = texture(_MainTex, flipV(uv1 + u._GameTime * u._MainTexSpeed)) * u._MainTexColor;
    vec4 texCol2 = texture(_MainTex2, flipV(uv2 + u._GameTime * u._MainTex2Speed)) * u._MainTex2Color;
    outColor = max(texCol1, texCol2) * u._Color * u._TuningColor * 2.0;
}
