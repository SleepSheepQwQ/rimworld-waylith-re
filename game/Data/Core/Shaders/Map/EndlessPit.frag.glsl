#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;   // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _NoiseTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec4 getNoise(vec2 uv)
{
    const float spd = 0.1;
    const float noiseScale = 0.45;
    vec2 uv1 = uv + vec2(u._AgeSecs * spd, 0.0);
    vec2 uv2 = uv - vec2(u._AgeSecs * spd, u._AgeSecs * spd);
    float mask = 1.0 - uv.y;
    return texture(_NoiseTex, flipV(uv1)) * texture(_NoiseTex, flipV(uv2)) * noiseScale * mask;
}

void main()
{
    vec2 uv = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec4 f = texture(_MainTex, flipV(uv)) * u._Color * vColor + getNoise(uv);
    if (f.a < 0.5)
        discard;
    outColor = f;
}
