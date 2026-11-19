#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _WorldSpaceCameraPos;   // global, set by MapRenderFeature
    float _DistortionIntensity;  // @default = 0.04
    float _DistortionSpeed;      // @default = 3.0
    float _GameTime;             // global, set by GlobalRendererUtility
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex; // @default = black
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;
layout(set = 2, binding = 3) uniform sampler2D _GrabDistortion;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

const float NearZoom = 15.0;
const float FarZoom = 65.0;

void main()
{
    vec4 sampled = texture(_MainTex, flipV(vec2(1.0, 2.0) * (vUv - vec2(0.0, u._GameTime * u._DistortionSpeed))));
    vec4 mask = texture(_MaskTex, flipV(vUv));

    float distortion = sampled.x * sampled.a * u._Color.a;
    float zoomFactor = mix(1.0, 0.0, (u._WorldSpaceCameraPos.y - NearZoom) / (FarZoom - NearZoom));

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    grabUv.y -= distortion * u._DistortionIntensity * zoomFactor * mask.r;
    outColor = texture(_GrabDistortion, grabUv);
}
