#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Main_TexOffset; // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _Main_TexScale;  // @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Mask_TexOffset; // @default = vec4(0.0, 0.0, 0.0, 0.0)
    vec4 _Mask_TexScale;  // @default = vec4(1.0, 1.0, 1.0, 1.0)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 uv = vUv * u._Mask_TexScale.xy + u._Mask_TexOffset.xy;
    uv.x = mix(uv.x, 1.0 - uv.x, u._Mask_TexOffset.z); // z set = x-flipped mask graphic
    float maskAlpha = texture(_MaskTex, flipV(uv)).a;

    vec2 xy = vUv * u._Main_TexScale.xy + u._Main_TexOffset.xy;
    vec4 c = texture(_MainTex, flipV(xy)) * u._Color;
    c.a *= maskAlpha;
    if (c.a < 0.05)
        discard;
    outColor = c;
}
