#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _distortionIntensity;  // @default = 0.05
    float _brightnessMultiplier; // @default = 1.0
    float _pulseSpeed;           // @default = 1.0
    float _AgeSecs;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _GrabDistortion;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec2 rotateUV(vec2 uv, float rotation)
{
    const float mid = 0.5;
    return vec2(
        cos(rotation) * (uv.x - mid) + sin(rotation) * (uv.y - mid) + mid,
        cos(rotation) * (uv.y - mid) - sin(rotation) * (uv.x - mid) + mid);
}

void main()
{
    float distortionBase = texture(_MainTex, flipV(vUv)).r;
    vec2 centeredOffset = vUv * 2.0 - 1.0;

    float sinVal = sin(u._AgeSecs * u._pulseSpeed);
    float distortion = sinVal * distortionBase * 2.0;

    vec2 offset = rotateUV(vUv, u._AgeSecs * 0.03 * distortion) - vUv;
    offset.x -= 0.03 * distortionBase * u._AgeSecs * centeredOffset.x;
    offset -= centeredOffset * distortion * u._distortionIntensity;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec4 bgcolorBase = texture(_GrabDistortion, grabUv);
    vec4 bgcolor = texture(_GrabDistortion, grabUv + vec2(offset.x, -offset.y));
    bgcolor = mix(bgcolor, bgcolor * u._brightnessMultiplier, abs(distortion));

    outColor = mix(bgcolorBase, bgcolor, u._Color.a);
}
