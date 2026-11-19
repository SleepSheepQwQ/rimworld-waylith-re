#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;       // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _dayColor;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _nightColor;  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;  // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _DustTex1_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;    // @default = 0.0
    float _DayPercent; // global, set by SkyManager
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DustTex1;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float adjustedDay = u._DayPercent <= 0.5 ? u._DayPercent * 2.0 : 2.0 - u._DayPercent * 2.0;
    vec4 dayColor = mix(u._nightColor, u._dayColor, adjustedDay);

    vec2 uv1 = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec2 uv2 = vUv * u._DustTex1_ST.zw + vec2(u._AgeSecs * 0.001, u._AgeSecs * 0.05);

    vec4 f = texture(_MainTex, flipV(uv1)) * u._Color * vColor * dayColor;
    if (f.a < 0.001)
        discard;
    vec4 dust = texture(_DustTex1, flipV(uv2));
    outColor = mix(f, f * dust, 0.8);
}
