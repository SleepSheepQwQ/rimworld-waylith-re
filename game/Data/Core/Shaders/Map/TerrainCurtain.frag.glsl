#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;           // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    // RGB multiplies the background texture, A encodes whether the background is used at all
    vec4 _BackgroundColor; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _ScreenParams;    // global, published by MapRenderFeature
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _BackgroundTex;
layout(set = 2, binding = 3) uniform sampler2D _MaskTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 skyboxUv = gl_FragCoord.xy / u._ScreenParams.xy;

    vec4 backgroundCol = texture(_BackgroundTex, skyboxUv) * u._BackgroundColor;
    vec4 foregroundCol = texture(_MainTex, flipV(vUv));
    vec4 col;
    col.rgb = (backgroundCol.rgb * (1.0 - foregroundCol.a) + foregroundCol.rgb * foregroundCol.a) * u._Color.rgb;
    col.rgb = mix(foregroundCol.rgb, col.rgb, step(0.5, u._BackgroundColor.a));
    col.a = texture(_MaskTex, flipV(vUv)).r * u._Color.a;
    outColor = col;
}
