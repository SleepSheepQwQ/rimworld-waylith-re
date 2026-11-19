#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;        // @default = 0.0
    float _ScanSpeed;      // @default = 0.5
    float _ScanSpeedAccel; // @default = 0.5
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _ScanTex;
layout(set = 2, binding = 3) uniform sampler2D _ScanMask;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 tex = texture(_MainTex, flipV(vUv));
    float scanMask = texture(_ScanMask, flipV(vUv)).r;

    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);

    float scanAccel = u._ScanSpeedAccel * u._AgeSecs * u._AgeSecs;
    float agedPolarOffset = u._AgeSecs * u._ScanSpeed + scanAccel;
    vec4 scanTex = texture(_ScanTex, flipV(vec2(atan(-centeredOffset.y, centeredOffset.x), distToCenter + agedPolarOffset)));

    outColor = clamp(tex + scanTex * scanMask, 0.0, 1.0);
}
