#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecs;       // @default = 0.0
    float _ScrollSpeed;   // @default = 0.1
    float _Thickness;     // @default = 10.0
    float _Distortion;    // @default = 1.0
    float _Detail;        // @default = 0.0
    float _VerticalScale; // @default = 1.3
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _SmokeTex1;
layout(set = 2, binding = 3) uniform sampler2D _SmokeTex2;
layout(set = 2, binding = 4) uniform sampler2D _MaskTex;
layout(set = 2, binding = 5) uniform sampler2D _DistortionTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float scroll = u._AgeSecs * u._ScrollSpeed;
    float mask = texture(_MaskTex, flipV(vUv)).r;
    float distortion = (texture(_DistortionTex, flipV(vUv * 0.5 + vec2(scroll * 0.8, scroll * 0.3))).r * 0.4 - 0.2) * u._Distortion;
    vec2 texCoord = vUv * vec2(0.45, 1.0) + distortion;
    float axisDistortion = pow(abs(vUv.x - 0.5) * 2.0, 2.0) * 0.2;
    vec4 tex1 = texture(_MainTex, flipV(texCoord * vec2(1.0, u._VerticalScale) + vec2(scroll - 0.5, 0.2 - axisDistortion)));
    vec4 tex2 = texture(_SmokeTex1, flipV(texCoord * vec2(1.0, u._VerticalScale) - vec2(scroll + 0.3, 0.55 - axisDistortion)));
    vec4 tex3 = texture(_SmokeTex2, flipV(texCoord + vec2(scroll + 0.5, -0.1)));
    vec4 col = u._Color - (tex3 * u._Detail);
    col.a = min(tex1 + tex2 + tex3 * 0.35, 0.05).r * mask * u._Thickness * u._Color.a;
    outColor = col;
}
