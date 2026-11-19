#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 1) uniform FragmentUniforms
{
    vec4 SunDir;
    mat4 WorldToCamera;
    mat4 ViewProj;
} uFragment;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _PlanetSunLightDirection; // global
} uMaterial;

const float PlanetRadius = 100.0;
const float GlowRadius   = 16.0;
const float Intensity    = 2.0;

const float GlowPowerFactor  = 3.0;
const float MinLight         = 0.02;
const float MaxLight         = 0.3;
const float LightPowerFactor = 2.0;
const vec3  GlowColor        = vec3(0.706, 0.839, 1.0);

void main()
{
    mat3 viewRotation = mat3(uFragment.WorldToCamera);
    vec3 cameraWorldPos = -(uFragment.WorldToCamera[3].xyz * viewRotation);

    // Point on the camera->fragment ray closest to the planet centre (origin).
    vec3 rayDir = normalize(vWorldPos - cameraWorldPos);
    vec3 closestPoint = cameraWorldPos - dot(cameraWorldPos, rayDir) * rayDir;
    float dist = length(closestPoint);

    // 1 at the planet edge, falling to 0 a glow-radius inside/outside it. Centre of the disk -> 0 (no flood).
    float nearOutlineFactor = clamp(1.0 - abs((dist - PlanetRadius) / GlowRadius), 0.0, 1.0);

    float light = dot(closestPoint / max(dist, 1e-4), -uMaterial._PlanetSunLightDirection.xyz) / 2.0 + 0.5;
    light = mix(MinLight, MaxLight, pow(light, LightPowerFactor));

    float glow = pow(nearOutlineFactor, GlowPowerFactor) * light * Intensity;

    outColor = vec4(GlowColor * glow, 1.0);
}
