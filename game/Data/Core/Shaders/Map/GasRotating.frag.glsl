#version 450

layout(location = 0) in vec4 vDensityAlphas;
layout(location = 1) in vec3 vUv;
layout(location = 2) in float vAlpha;
layout(location = 3) in vec2 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _RotationSpeed;   // @default = 1.0
    float _AgeSecsPausable; // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _ToxGasTex;
layout(set = 2, binding = 3) uniform sampler2D _RotGasTex;
layout(set = 2, binding = 4) uniform sampler2D _DeadlifeDustTex;
layout(set = 2, binding = 5) uniform sampler2D _RippleTex; // global

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

float rand(float x) { return abs(fract(sin(x) * 43758.5453)); }

vec2 rotateUV(vec2 uv, float rotation)
{
    const float mid = 0.5;
    return vec2(
        cos(rotation) * (uv.x - mid) + sin(rotation) * (uv.y - mid) + mid,
        cos(rotation) * (uv.y - mid) - sin(rotation) * (uv.x - mid) + mid);
}

vec4 getDLD(vec2 rotatedUv)
{
    const vec4 dldColor = vec4(0.01, 0.05, 0.2, 1.0);
    vec4 dld = texture(_DeadlifeDustTex, flipV(rotatedUv)) * vDensityAlphas.w * dldColor;
    if (vDensityAlphas.w < 0.1)
        return dld;
    vec2 worldNoiseOffset = vec2(
        cos(u._AgeSecsPausable / 5.0 + vUv.z * 2.0),
        -u._AgeSecsPausable * 0.25 + vUv.z * 0.1);
    vec4 noise = texture(_RippleTex, flipV((vWorldPos + worldNoiseOffset) * 0.3));
    float filteredNoise = step(noise.r, 0.2) * 5.0 * vDensityAlphas.w;
    if (filteredNoise > dld.a)
        return vec4(0.0, 0.0, 0.0, filteredNoise);
    return dld;
}

void main()
{
    const float minRotationFactor = 0.2;
    const float maxRotationFactor = 1.0;
    float index = vUv.z;
    float rotationSpeedFactorThisCell = min(rand(index) + minRotationFactor, maxRotationFactor);
    if (rand(index) < 0.5)
        rotationSpeedFactorThisCell *= -1.0;
    rotationSpeedFactorThisCell *= 1.0 - vDensityAlphas.w * 0.5; // deadlife barely rotates
    float rot = u._AgeSecsPausable * u._RotationSpeed * rotationSpeedFactorThisCell + index;

    const vec4 smokeColor = vec4(200.0 / 255.0, 200.0 / 255.0, 200.0 / 255.0, 1.0);
    const vec4 toxColor = vec4(180.0 / 255.0, 214.0 / 255.0, 24.0 / 255.0, 1.0);
    const vec4 rotColor = vec4(214.0 / 255.0, 90.0 / 255.0, 24.0 / 255.0, 1.0);

    vec2 rotatedUv = rotateUV(vUv.xy, rot);
    vec4 smoke = texture(_MainTex, flipV(rotatedUv)) * vDensityAlphas.x * smokeColor;
    vec4 tox = texture(_ToxGasTex, flipV(rotatedUv)) * vDensityAlphas.y * toxColor;
    vec4 rotGas = texture(_RotGasTex, flipV(rotatedUv)) * vDensityAlphas.z * rotColor;
    vec4 dld = getDLD(rotatedUv);
    outColor = (smoke + tox + rotGas + dld) * u._Color * vec4(1.0, 1.0, 1.0, vAlpha);
}
