#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _ColorTwo;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black

void main()
{
    vec2 uv = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec4 f = texture(_MainTex, uv) * u._Color * vColor;
    vec4 mask = texture(_MaskTex, uv);
    vec4 blended = u._ColorTwo * mask;
    float p = mask.a * u._ColorTwo.a;
    vec4 lerpd = mix(f, blended, p);
    lerpd.a = f.a;
    if (f.a < 0.25)
        discard;
    outColor = lerpd;
}
