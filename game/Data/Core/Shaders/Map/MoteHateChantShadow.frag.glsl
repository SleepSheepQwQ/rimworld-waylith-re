#version 450

// Purely procedural shadow mote
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                        // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _shadowMultiplier;            // @default = 1.0
    float _AgeSecs;                     // @default = 0.0
    float _RandomPerObjectOffsetRandom; // @default = 0.0
} u;

vec2 rand(vec2 p)
{
    return fract(sin(vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)))) * 43758.5453);
}

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);
    vec2 distUv = 2.0 * vUv;
    float t = u._AgeSecs * 0.3 - rand(vec2(mod(u._RandomPerObjectOffsetRandom, 1000.0))).x;

    for (int i = 0; i < 2; i++)
    {
        distUv += cos(distUv.yx * 3.0 + vec2(t, 1.6)) / 4.0;
        distUv += sin(distUv.yx + t + vec2(1.6, 0.0)) / 4.0;
        distUv *= 1.4;
        distUv += rand(vec2(mod(u._RandomPerObjectOffsetRandom, 2000.0)));
    }

    vec4 col = length(mod(distUv, 2.0) - 1.0) * vec4(0.0, 0.0, 0.0, u._shadowMultiplier);
    col = mix(col, vec4(0.0), distToCenter * distToCenter * 1.8);
    outColor = col * u._Color.a;
}
