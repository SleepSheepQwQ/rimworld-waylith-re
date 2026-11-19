#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vObjectWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _WorldSpaceCameraPos;       // global, MapRenderFeature
    float _AgeSecs;                  // @default = 0.0
    float _DestructionStartAgeSecs;  // @default = 999999.0
    float _AmbientShake;             // @default = 0.0
    float _Parallax;                 // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _DustTex;
layout(set = 2, binding = 4) uniform sampler2D _DissolveTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float t20 = u._AgeSecs / 20.0;
    float maskVal = texture(_MaskTex, flipV(vUv)).r;
    float dissolveMaskVal = texture(_DissolveTex, flipV(vUv)).r;
    float destructionAlpha = clamp((u._AgeSecs - u._DestructionStartAgeSecs) / 11.0, 0.0, 1.0);
    if (dissolveMaskVal - destructionAlpha < 0.0)
        discard;
    vec4 baseColor = texture(_MainTex, flipV(vUv));
    baseColor = vec4(baseColor.rgb * 0.7, baseColor.a);
    if (baseColor.a - 1.0 < 0.0)
        discard;

    const float parallax = 2.0;
    vec4 dustAccum = vec4(0.0);
    for (int layer = 0; layer < int(parallax); layer++)
    {
        vec2 camPos = u._WorldSpaceCameraPos.xz / 150.0;
        vec2 camDistance = (camPos - vObjectWorld) * 4.0;
        vec2 dustUv = vUv + camDistance;
        float layer1 = float(layer + 1);
        dustUv += 50.0 * vec2(cos(t20 * layer1), sin(t20));
        vec4 plxDust = texture(_DustTex, flipV(dustUv));
        plxDust = mix(vec4(0.0), plxDust, plxDust.a) * maskVal;
        plxDust *= 40.0 * ((sin(t20 * layer1) + 1.0) / 2.0);
        dustAccum += plxDust * (parallax - float(layer)) / parallax * 0.2;
    }
    outColor = baseColor + dustAccum;
}
