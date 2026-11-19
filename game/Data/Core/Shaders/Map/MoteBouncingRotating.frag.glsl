#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _bounceSpeed;      // @default = 1.0
    float _bounceAmplitude;  // @default = 1.0
    float _rotationSpeed;    // @default = 1.0
    float _rotationAmplitude;// @default = 1.0
    float _AgeSecs;          // @default = 0.0
    float _RotationFromOffset; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _BounceTex; // @vertex @default = black
layout(set = 2, binding = 3) uniform sampler2D _RotationTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

float Offset()
{
    return textureLod(_BounceTex, vec2(u._AgeSecs * u._bounceSpeed, 0.0), 0.0).r * u._bounceAmplitude;
}

vec2 rotateUV(vec2 uv, float rotation)
{
    float mid = 0.5;
    return vec2(
        cos(rotation) * (uv.x - mid) + sin(rotation) * (uv.y - mid) + mid,
        cos(rotation) * (uv.y - mid) - sin(rotation) * (uv.x - mid) + mid
    );
}

void main()
{
    float offset = Offset();
    vec2 rotUv = vec2(u._AgeSecs * u._rotationSpeed, 0.0);
    float rot = (texture(_RotationTex, flipV(rotUv)).r * 2.0 - 1.0) * u._rotationAmplitude + offset * u._RotationFromOffset;

    vec4 tex = texture(_MainTex, flipV(rotateUV(vUv, rot)));
    outColor = tex * u._Color;
}
