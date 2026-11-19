#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vTexCoordRotated;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _GameSeconds;       // global, set by TickManager
    float _smokeScale;        // @default = 1.0
    float _smokeScrollSpeed;  // @default = 1.0
    float _rotationSpeed;     // @default = 1.0
    float _smokeAmount;       // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _ScanTex; // @default = black
layout(set = 2, binding = 3) uniform sampler2D _SmokeTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float main = texture(_MainTex, flipV(vUv)).a;
    float smokey = texture(_SmokeTex, flipV(vUv * u._smokeScale + vec2(u._GameSeconds, u._GameSeconds) * u._smokeScrollSpeed)).r * u._smokeAmount;
    float scan = texture(_ScanTex, flipV(vTexCoordRotated)).a;

    vec4 f = (main + max(scan - smokey, 0.0)) * u._Color;
    outColor = clamp(f, 0.0, 1.0);
}
