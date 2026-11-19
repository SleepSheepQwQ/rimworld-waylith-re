#version 450

layout(location = 0) in vec4 vColor; // terrain vertex colour (day/night + glow)
layout(location = 1) in vec2 vUv;    // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _GameTime;                // global, set every frame by GlobalRendererUtility
    float _DistortionFrequency;     // @default = 1.0
    float _DistortionAmplitude;     // @default = 0.05
    float _DistortionSpeed;         // @default = 0.1
    float _LavaBrightness;          // @default = 0.0
    float _BrightSpotSpeed;         // @default = 0.01
    float _BrightSpotScale;         // @default = 0.4
    float _BrightSpotThresholdMin;  // @default = 0.0
    float _BrightSpotThresholdMax;  // @default = 0.7
    float _BrightSpotWarpFrequency; // @default = 0.1
    float _BrightSpotWarpAmplitude; // @default = 0.5
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _AlphaAddTex;
layout(set = 2, binding = 3) uniform sampler2D _NoiseTex;
layout(set = 2, binding = 4) uniform sampler2D _MaskTex;

vec2 flipV(vec2 uv) { return vec2(uv.x, 1.0 - uv.y); } 

const float PI = 3.141592;
const float TAU = PI * 2.0;

// Manual smoothstep: matches HLSL's raw formula for reversed (edge0 > edge1) edges, which GLSL smoothstep leaves undefined.
float sstep(float e0, float e1, float x)
{
    float t = clamp((x - e0) / (e1 - e0), 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

vec2 distort(vec2 uv, float timeOffset, float frequency, float speed, float amplitude)
{
    uv.x += sin(uv.y * TAU * frequency + (u._GameTime + timeOffset) * speed) * amplitude;
    uv.y += sin(uv.x * TAU * frequency + (u._GameTime + timeOffset) * speed) * amplitude;
    return uv;
}

float hash(vec2 p)
{
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

float fastCollapseSine(float t, float collapseSpeed)
{
    float s = sin(t);
    return cos(t) < 0.0 ? pow(max(s, 0.0), collapseSpeed) : s;
}

float bubble(vec2 uv, float time, float frequency, float radius)
{
    vec2 scaledUV = uv * frequency;
    vec2 tile0 = floor(scaledUV);
    vec2 tile1 = floor(scaledUV + 1.0);
    float hash0 = hash(tile0);
    float hash1 = hash(tile1);
    vec2 offset = 1.0 - 2.0 * (1.0 - radius * 2.0) * vec2(hash0, hash1);
    vec2 bubbleCenter = 0.5 + offset * 0.5;

    float bubbleTime = fract(time * 0.1 + hash0); // each bubble spawns at a different time
    float bubbleRadius = sstep(0.0, 1.0, 0.5 + 0.5 * fastCollapseSine(bubbleTime * 3.1415, 100.0)); // expand & fade

    float dist = 2.0 * length(bubbleCenter - (scaledUV - tile0));
    float bubbleShape = sstep(0.05, 0.04, dist - bubbleRadius * radius);

    return (bubbleTime < 0.5 ? (1.0 - 4.0 * dist) : 4.0 * dist) * bubbleShape * bubbleRadius;
}

// Verbatim from TerrainRoughFadeFactor.cginc RoughFade.
float roughFade(vec2 position, float rawAlpha)
{
    const float noiseImportance = 0.6;
    const float exPart = 1.0 - noiseImportance;
    float noise = texture(_AlphaAddTex, flipV(position * 2.0)).r * 0.333
                + texture(_AlphaAddTex, flipV(position * 5.0)).g * 0.333
                + texture(_AlphaAddTex, flipV(position * 10.0)).b * 0.333;
    float alpha = mix(rawAlpha, noise, noiseImportance);
    alpha -= (1.0 - exPart) * 0.5;
    alpha /= exPart;
    const float clampSpeed = 1.5;
    alpha = clamp(alpha, max(rawAlpha * clampSpeed - clampSpeed + 1.0, 0.0), min(rawAlpha * clampSpeed, 1.0));
    return alpha;
}

void main()
{
    const vec4 LAVA_COLOR = vec4(196.0, 177.0, 45.0, 255.0) / 255.0;

    vec2 uvRaw = vUv / 16.0;

    vec2 uv = distort(
        distort(uvRaw, uvRaw.x + uvRaw.y, u._DistortionFrequency, u._DistortionSpeed, u._DistortionAmplitude * 0.3),
        0.3, u._DistortionFrequency * 0.3, u._DistortionSpeed * 0.3, u._DistortionAmplitude * 0.7);

    float scaledTime = u._GameTime * u._BrightSpotSpeed;
    vec4 mainTexCol = texture(_MainTex, flipV(uv));

    float displacement = texture(_NoiseTex, flipV(uv * u._BrightSpotWarpFrequency)).r * u._BrightSpotWarpAmplitude;
    float brightSpotSample = sstep(u._BrightSpotThresholdMin, u._BrightSpotThresholdMax,
        texture(_NoiseTex, flipV(displacement + uv * u._BrightSpotScale + scaledTime)).r *
        texture(_NoiseTex, flipV(displacement + uv * u._BrightSpotScale - scaledTime)).r);

    float rockMask = texture(_MaskTex, flipV(uv)).r;
    brightSpotSample *= rockMask;

    vec4 col = mix(mainTexCol, LAVA_COLOR, brightSpotSample);
    col = mix(col, LAVA_COLOR, bubble(uvRaw, u._GameTime, 4.0, 0.2) * rockMask);
    col *= vColor;
    col.a = roughFade(uvRaw, col.a);

    outColor = col * u._Color * (1.0 + rockMask * u._LavaBrightness);
}
