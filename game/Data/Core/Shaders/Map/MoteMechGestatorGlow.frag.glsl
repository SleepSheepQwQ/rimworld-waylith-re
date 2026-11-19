#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;         // @default = 0.0
    float _MistScrollSpeed; // @default = 0.2
    float _ScanOffset;      // @default = 0.0
    float _ScanScale;       // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _MistTex;
layout(set = 2, binding = 3) uniform sampler2D _MistOpacityTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 tex = texture(_MainTex, flipV(vUv));
    float mist = texture(_MistTex, flipV(vUv * 0.7 - vec2(0.0, u._AgeSecs * u._MistScrollSpeed))).r;
    float mistAlpha = texture(_MistOpacityTex, flipV(vUv)).a;

    float centerOffset = sin((vUv.x + u._ScanOffset) * u._ScanScale);

    float scanTarget = abs(1.5 - mod(u._AgeSecs / 2.0, 1.4)) - 0.1 + centerOffset * 0.1;
    float scanFinal = pow(1.0 - abs(scanTarget - vUv.y), 5.0);

    vec4 f = (tex.a * 0.6 * scanFinal + mist * mistAlpha * 0.55) * u._Color;
    outColor = clamp(f, 0.0, 1.0);
}
