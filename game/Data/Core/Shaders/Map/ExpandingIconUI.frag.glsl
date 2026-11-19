#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _ColorTwo;   // @color @default = vec4(0.1, 0.9, 0.1, 1.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 getOffsetUV(vec2 uv, vec2 offset)
{
    return clamp(uv + offset, vec2(0.0), vec2(1.0));
}

void main()
{
    const float thickness = 0.05;
    vec2 uv = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec2 inner = clamp(vec2(0.5) + (uv - vec2(0.5)) * 1.1, vec2(0.0), vec2(1.0));
    vec4 f = texture(_MainTex, inner) * u._Color;

    vec4 l = texture(_MainTex, getOffsetUV(uv, vec2(thickness, 0.0)));
    vec4 r = texture(_MainTex, getOffsetUV(uv, vec2(-thickness, 0.0)));
    vec4 up = texture(_MainTex, getOffsetUV(uv, vec2(0.0, thickness)));
    vec4 d = texture(_MainTex, getOffsetUV(uv, vec2(0.0, -thickness)));
    vec4 m = l + r + up + d;
    m.a = clamp(m.a, 0.0, 1.0);
    m.a = clamp(m.a, 0.0, u._Color.a);
    m.rgb = u._ColorTwo.rgb;
    m *= m.a - f.a;
    outColor = f + m;
}
