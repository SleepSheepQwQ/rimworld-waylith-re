#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Time;  // global, published by MapRenderFeature
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _NoiseTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _GrabInvisible;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

const float noiseScrollSpeed = 0.1;
const float noiseScale = 1.3;
const float noiseIntensity = 0.3;
const float intensity = 0.45;

void main()
{
    vec4 noise = texture(_NoiseTex, flipV(vUv * vec2(noiseScale, 2.0) + vec2(u._Time.y * noiseScrollSpeed, 0.0)));
    vec4 tex = texture(_MainTex, flipV(vUv)) - 0.015;
    if (tex.a - 0.02 < 0.0)
        discard;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabInvisible, 0));
    grabUv += noise.xy * 0.04 - 0.02;

    vec4 result = texture(_GrabInvisible, grabUv);
    float grayscale = dot(tex.rgb, vec3(0.299, 0.587, 0.114));
    result += -pow((1.0 - grayscale) * intensity, 2.0) * 1.5 + noise * noise * noiseIntensity * u._Color;

    outColor = result;
}
