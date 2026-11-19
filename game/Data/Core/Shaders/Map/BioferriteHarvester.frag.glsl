#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;   // @default = 0.0
    float _Working;   // @default = 0.0
    float _Rotation;  // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MaskTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec4 getGlow()
{
    const float scanSpeed = 3.0;
    float glow;
    if (mod(u._Rotation, 2.0) == 0.0) // north/south: flip so the scan always starts at the same visual end
        glow = sin(u._AgeSecs * scanSpeed + (vUv.y * 5.0) * -(u._Rotation - 1.0));
    else
        glow = sin(u._AgeSecs * scanSpeed + (vUv.x * 5.0) * -(u._Rotation - 2.0));
    glow = (glow + 1.0) * 0.5;
    return texture(_MaskTex, flipV(vUv)) * glow * u._Working;
}

void main()
{
    vec4 f = texture(_MainTex, flipV(vUv)) * u._Color * vColor;
    if (f.a < 0.5)
        discard;
    outColor = f + getGlow();
}
