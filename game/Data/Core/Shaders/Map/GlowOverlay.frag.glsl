#version 450

layout(location = 4) in vec3 vPosLocal;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _TuningColor;   // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTexColor;  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex2Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _MainTex2_ST;   // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _NoiseTex_ST;   // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec2 _NoiseSpeed;    // @default = vec2(1.0, 1.0)
    float _GlowMin;      // @default = 0.0
    float _GlowMax;      // @default = 1.0
    float _GameTime;     // global, set by GlobalRendererUtility
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MainTex2;
layout(set = 2, binding = 3) uniform sampler2D _NoiseTex;

const float DistortionFrequency = 0.3;
const float DistortionAmplitude = 0.1;
const float DistortionSpeed = 0.175;
const float TAU = 3.141592 * 2.0;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec2 distort(vec2 uv, float timeOffset)
{
    uv.x += sin(uv.y * TAU * DistortionFrequency + (u._GameTime + timeOffset) * DistortionSpeed) * DistortionAmplitude;
    uv.y += sin(uv.x * TAU * DistortionFrequency + (u._GameTime + timeOffset) * DistortionSpeed) * DistortionAmplitude;
    return uv;
}

vec4 layer(sampler2D tex, vec4 multColor, vec2 uv, vec4 thresholdScaleTiling, float threshold, float timeOffset)
{
    vec4 col = texture(tex, flipV(distort(uv, timeOffset))) * multColor;
    float noiseSample = texture(_NoiseTex, flipV(distort(uv * thresholdScaleTiling.xy + thresholdScaleTiling.zw, timeOffset))).r;
    return vec4(col.rgb, mix(col.a, 0.0, smoothstep(threshold - 0.1, threshold, noiseSample)));
}

void main()
{
    vec2 uv1 = (vPosLocal.xz / 8.0) * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec2 uv2 = uv1 * u._MainTex2_ST.zw + u._MainTex2_ST.xy;
    vec2 uvNoise1 = uv1 * u._NoiseTex_ST.zw + u._NoiseTex_ST.xy;

    vec4 col = vec4(0.0);
    col += layer(_MainTex, u._MainTexColor, uv1, vec4(1.0, 1.0, 0.0, 0.0), 0.55, 0.0);
    col += layer(_MainTex2, u._MainTexColor, 0.2 + uv1 * 0.5, vec4(-1.0, 1.0, 0.2, 0.2), 0.55, 1.8);
    col += layer(_MainTex, u._MainTexColor, 0.6 + uv2 * 1.5, vec4(1.0, -1.0, 0.6, 0.6), 0.55, 3.6);
    col += layer(_MainTex2, u._MainTex2Color, uv2, vec4(-1.0, -1.0, 0.0, 0.0), 0.55, 4.8);

    float noiseSample = texture(_NoiseTex, flipV(distort(uvNoise1, 0.0) + u._GameTime * u._NoiseSpeed)).r;
    col.rgb *= mix(u._GlowMin, u._GlowMax, noiseSample);
    outColor = vec4(col.rgb * col.a * 1.2, 1.0);
}
