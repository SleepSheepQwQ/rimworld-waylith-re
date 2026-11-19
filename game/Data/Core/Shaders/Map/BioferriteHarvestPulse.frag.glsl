#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;               // @default = 0.0
    float _ChargeSpeed;           // @default = 1.4
    float _DistortionScrollSpeed; // @default = 0.45
    float _DistortionIntensity;   // @default = 0.15
    float _Delay;                 // @default = 0.15
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DistortionTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    if (u._AgeSecs < u._Delay)
        discard;
    float distortionProgress = fract(u._AgeSecs * u._DistortionScrollSpeed);
    float distortion = texture(_DistortionTex, flipV(vUv + vec2(0.0, distortionProgress - 0.5))).r * u._DistortionIntensity;
    float chargeProgress = fract((-u._AgeSecs + u._Delay) * u._ChargeSpeed);
    vec4 tex = texture(_MainTex, flipV(vUv + vec2(0.0, chargeProgress - 0.5 + distortion)));
    if (vUv.y <= 0.1)
        tex.a *= vUv.y / 0.1;
    else if (vUv.y >= 0.9)
        tex.a *= 1.0 - (vUv.y - 0.9) / 0.1;
    outColor = tex * u._Color;
}
