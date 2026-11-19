#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vRand;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _FlickerFrequency;          // @default = 1.0
    float _FlickerAmount;             // @default = 1.0
    float _InnerCircleSize;           // @default = 1.0
    float _InnerCircleShimmerAmount;  // @default = 0.1
    float _InnerCircleIntensity;      // @default = 0.2
    float _AgeSecs;                   // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _RandomTex; // @vertex @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 tex = texture(_MainTex, flipV(vUv));

    vec2 centeredOffset = vUv * 2.0 - 1.0 + vec2(0.0, 0.23);
    float distToCenter = length(centeredOffset) + (vRand.g * u._InnerCircleShimmerAmount - u._InnerCircleShimmerAmount * 0.5);
    float innerCircle = step(distToCenter, u._InnerCircleSize);

    vec4 f = tex * (1.0 - vRand.r * u._FlickerAmount) * u._Color + innerCircle * u._InnerCircleIntensity;
    outColor = clamp(f, 0.0, 1.0);
}
