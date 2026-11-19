#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                        // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _distortionTint;               // @default = vec4(0.0, 0.0, 0.0, 0.0)
    float _AgeSecs;
    float _pulseSpeed;                  // @default = 1.0
    float _distortionIntensity;         // @default = 0.05
    float _snakeParticlesFactor;        // @default = 1.0
    float _RandomPerObjectOffsetRandom; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;
layout(set = 2, binding = 3) uniform sampler2D _GrabDistortion;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec2 rand2(vec2 p)
{
    return fract(sin(vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)))) * 43758.5453);
}

float smin(float a, float b, float k)
{
    float f = max(0.0, 1.0 - abs(b - a) / k);
    return min(a, b) - k * 0.25 * f * f;
}

vec4 distortionPass(vec2 centeredOffset, float distToCenter, vec4 bgcolorBase, vec2 grabUv, out float visible)
{
    visible = 0.0;
    if (u._distortionIntensity == 0.0)
        return bgcolorBase;

    vec2 distUv = vUv;
    distUv.x += 0.04 * cos(12.0 * distUv.y + 0.8 * sin(u._AgeSecs));
    distUv.x += 0.04 * cos(10.0 * distUv.y + 0.5 * u._AgeSecs) * -0.3;
    distUv.y += 0.04 * sin(10.0 * distUv.x + 0.5 * cos(u._AgeSecs * 1.2));
    distUv.y += 0.04 * sin(5.0 * distUv.x + 0.5 * -cos(u._AgeSecs * 1.2));

    float intensity = u._distortionIntensity * distToCenter * distToCenter * 2.5 * u._Color.a;
    float distortion = texture(_MainTex, flipV(distUv)).r;

    if (distortion - 0.2 < 0.0)
        return bgcolorBase;

    vec2 offset = sin(centeredOffset * pow(distortion * intensity, 3.0));
    vec4 bgcolor = texture(_GrabDistortion, grabUv + vec2(offset.x, -offset.y));
    visible = 1.0;
    return mix(bgcolor, bgcolor + u._distortionTint, pow(abs(distortion), 1.5));
}

vec4 particlesPass(float distToCenter)
{
    vec2 st = (vUv * 0.75 + 3.0) * 15.0;
    vec2 intSt = floor(st);
    vec2 fracSt = fract(st);

    float minDist = 40.0;

    for (int j = -2; j <= 2; j++)
    {
        for (int k = -2; k <= 2; k++)
        {
            vec2 neighbor = vec2(float(k), float(j));
            vec2 offset = rand2(intSt + neighbor + mod(u._RandomPerObjectOffsetRandom, 500.0)) * 0.7;
            offset = 0.5 + 0.6 * sin(u._AgeSecs * 1.2 + 6.2831 * offset) * -cos(u._AgeSecs * 1.5 * offset * 1.5) * 1.3;
            vec2 pos = neighbor + offset - fracSt;
            float dist = length(pos) * 0.4;
            minDist = smin(minDist, dist, 0.54 * 1.0 / u._snakeParticlesFactor);
        }
    }

    float stepped = smoothstep(0.0, fwidth(minDist * 20.0 / 7.0) * 1.6, minDist * 0.22);

    vec4 fragCol = vec4(vec3(stepped), 1.0);
    fragCol = mix(vec4(0.0), fragCol.r * vec4(0.0, 0.0, 0.0, 0.9), 1.2 * sin(distToCenter * 3.0));
    fragCol = mix(vec4(0.0), fragCol, distToCenter * distToCenter);

    fragCol *= texture(_MaskTex, flipV(vUv));

    if (fragCol.a - 0.2 < 0.0)
        return vec4(0.0);
    return fragCol * u._Color.a;
}

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    float distToCenter = length(centeredOffset);

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDistortion, 0));
    vec4 bgcolorBase = texture(_GrabDistortion, grabUv);

    float distortVisible;
    vec4 base = distortionPass(centeredOffset, distToCenter, bgcolorBase, grabUv, distortVisible);
    vec4 particles = particlesPass(distToCenter);

    if (distortVisible == 0.0 && particles.a == 0.0)
        discard;

    outColor = vec4(mix(base.rgb, particles.rgb, particles.a), 1.0);
}
