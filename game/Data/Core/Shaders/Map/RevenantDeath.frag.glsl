#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    float _AgeSecs; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _DissolveTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    const float dissolveTime = 5.0;
    float ageAlpha = clamp(u._AgeSecs / dissolveTime, 0.0, 1.0);
    vec4 dissolve = texture(_DissolveTex, flipV(vUv));
    if (any(lessThan(dissolve - ageAlpha, vec4(0.0))))
        discard;
    outColor = texture(_MainTex, flipV(vUv));
}
