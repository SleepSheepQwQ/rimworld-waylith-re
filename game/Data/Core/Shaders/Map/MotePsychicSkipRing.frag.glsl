#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _distortionIntensity;  // @default = 0.05
    float _brightnessMultiplier; // @default = 1.0
    float _innerRingSize;        // @default = 0.6
    float _outerRingSize;        // @default = 0.9
    float _inTime;               // @default = 0.4
    float _solidTime;            // @default = 0.5
    float _outTime;              // @default = 0.3
    float _AgeSecs;
    float _AgeOffset;            // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _GrabDistortion;

float invLerp(float from, float to, float value)
{
    return (value - from) / (to - from);
}

float ageToIntensity()
{
    float age = u._AgeSecs + u._AgeOffset;

    if (age < u._inTime)
        return age / u._inTime;
    else if (age >= u._inTime + u._solidTime)
        return 1.0 - (age - (u._inTime + u._solidTime)) / u._outTime;

    return 1.0;
}

vec2 rotateUV(vec2 uv, float rotation)
{
    const float mid = 0.5;
    return vec2(
        cos(rotation) * (uv.x - mid) + sin(rotation) * (uv.y - mid) + mid,
        cos(rotation) * (uv.y - mid) - sin(rotation) * (uv.x - mid) + mid);
}

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);

    float opacity = ageToIntensity();

    float innerBorder = u._innerRingSize * opacity;
    float ringCenterDistance = pow(invLerp(innerBorder, u._outerRingSize, distToCenter), 1.5);
    if (ringCenterDistance > u._outerRingSize || distToCenter <= u._innerRingSize)
        discard;

    float extraBrightness = pow(1.0 - ringCenterDistance, 18.0) * 0.5;
    float distortion = pow(1.0 - abs(ringCenterDistance), 2.0) * opacity * u._Color.a;

    vec2 offset = centeredOffset * distortion * u._distortionIntensity + (rotateUV(vUv, -0.01 * distortion) - vUv);

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec4 bgcolor = texture(_GrabDistortion, grabUv + vec2(offset.x, -offset.y));
    outColor = mix(bgcolor, bgcolor * (u._brightnessMultiplier + extraBrightness), abs(distortion));
}
