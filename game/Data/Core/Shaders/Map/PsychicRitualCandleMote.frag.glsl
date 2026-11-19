#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _FlameTex_ST;      // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _Interval;        // @default = 0.1
    float _RandomPerObject; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _FlameTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 color = texture(_FlameTex, flipV(vUv * u._FlameTex_ST.zw + u._FlameTex_ST.xy));
    float pulse = (sin((u._AgeSecs + (u._RandomPerObject / 1000.0)) / u._Interval) + 1.0) / 2.0;
    float pulsePower = (pulse * 0.125) + 0.875;
    outColor = clamp(color * pulsePower, 0.0, 1.0);
}
