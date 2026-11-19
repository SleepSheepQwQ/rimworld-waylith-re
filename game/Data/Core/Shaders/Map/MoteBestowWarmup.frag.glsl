#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                 // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _distortionIntensity;  // @default = 0.05
    float _brightnessMultiplier; // @default = 1.0
    float _pulseSpeed;           // @default = 1.0
    float _AgeSecs;              // per-draw, from Graphic_MoteWithAgeSecs
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _GrabDistortion; // scene snapshot at the Distortion grab point

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float distortionBase = texture(_MainTex, flipV(vUv)).r;
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);

    float distortion = sin(u._AgeSecs + u._pulseSpeed * distToCenter) * distortionBase * u._distortionIntensity;

    // quad UV +y is world north = screen up, but the grab is sampled in gl_FragCoord space (+y down),
    // so the offset's y flips to stay radially outward on screen.
    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec4 bgcolorBase = texture(_GrabDistortion, grabUv);
    vec4 bgcolor = texture(_GrabDistortion, grabUv + vec2(centeredOffset.x, -centeredOffset.y) * distortion);
    bgcolor = mix(bgcolor, bgcolor * u._brightnessMultiplier, abs(distortion));

    outColor = mix(bgcolorBase, bgcolor, u._Color.a);
}
