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
    vec2 _Seed;            // material SetVector(_Seed)
    float _FinalOpacityMult; // material SetFloat(_FinalOpacityMult) - 0 in the selected (normal) view
    vec4 _PlanetSunLightDirection; // globals from WorldRendererUtility / GlobalRendererUtility
    float _PlanetSunLightEnabled;
    float _BackgroundModeEnabled;
    float _GameTime;
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _NoiseTex;
layout(set = 2, binding = 2) uniform sampler2D _SunsetColorRamp;
layout(set = 2, binding = 3) uniform sampler2D _CloudTex;

const vec3 CloudColor      = vec3(0.9, 0.9, 0.95);
const vec3 NightCloudColor = vec3(0.25, 0.25, 0.35);
const vec3 NightColor      = vec3(0.0, 0.12, 0.2);
const vec3 ShadowTint      = vec3(0.4, 0.4, 0.4);

const float AtmosphereOpacityBoost = 0.40;
const float AtmosphereOpacityBoostBackground = 0.3;
const float SunsetBrightness       = 0.6;
const float SunsetStripeThickness  = 0.2;
const float WraparoundAmount       = 0.2;

const float CloudMoveSpeed           = 0.0002;
const float CloudDanceSpeed          = 0.015;
const float CloudShadowDistance      = 0.01;
const float CloudOpacity             = 1.0;
const float CloudDomainWarpFrequency = 10.0;
const float CloudDomainWarpIntensity = 0.0015;

// Returns (uv.x, uv.y, cover) where cover in [0,1] is the cloud density.
vec3 CalculateCloudCover(float lod, vec2 uv, vec3 normal)
{
    uv.x += uMaterial._GameTime * CloudMoveSpeed + uMaterial._Seed.x;
    vec2 distortionUV = uv * CloudDomainWarpFrequency;
    distortionUV.y += uMaterial._GameTime * CloudDanceSpeed + uMaterial._Seed.x;
    distortionUV.x *= 2.0;
    vec2 distortion = 1.0 - 2.0 * texture(_NoiseTex, vec2(distortionUV.x, -distortionUV.y)).xy;
    uv += distortion * CloudDomainWarpIntensity;
    float u = mix(uv.x, 1.0 - uv.x, step(0.5, fract(uMaterial._Seed.y)));
    float cover = textureLod(_CloudTex, vec2(u, 1.0 - uv.y), lod).r;
    cover = smoothstep(0.2, 0.5, pow(cover, 0.5)) * cover;
    return vec3(uv.x, uv.y, cover);
}

void main()
{
    vec3 normal = normalize(vWorldPos);
    vec3 sunDir = uMaterial._PlanetSunLightDirection.xyz;
    float NdotL = dot(normal, -sunDir);

    mat3 viewRotation = mat3(uFragment.WorldToCamera);
    vec3 viewTranslation = uFragment.WorldToCamera[3].xyz;
    vec3 cameraWorldPos = -(viewTranslation * viewRotation);
    float NdotV = dot(normal, normalize(cameraWorldPos));

    float sunsetPercent = 1.0 - smoothstep(-SunsetStripeThickness, SunsetStripeThickness, NdotL);

    vec3 atmosphere = textureLod(_SunsetColorRamp, vec2(sunsetPercent, 0.0), 0.0).rgb;
    float lod = 0.0;

    // Sample clouds.
    float angle = atan(normal.z, normal.x) / 3.1415;
    vec2 uv = 0.5 + 0.5 * vec2(angle, normal.y);
    float cloudSample = CalculateCloudCover(lod, uv, normal).z;

    // Sample again offset along the sun direction to fake self-shadowing.
    vec3 normalS = normal - sunDir * CloudShadowDistance;
    angle = atan(normalS.z, normalS.x) / 3.1415;
    vec2 uvS = 0.5 + 0.5 * vec2(angle, normalS.y);
    float shadow = CalculateCloudCover(lod, uvS, normalS).z;

    float NdotVpos = max(NdotV, 0.0);
    float atmosphereBoost  = mix(AtmosphereOpacityBoost, AtmosphereOpacityBoostBackground, uMaterial._BackgroundModeEnabled);
    float primaryFresnel   = clamp(1.0 - pow(NdotVpos, 0.1) + atmosphereBoost, 0.0, 1.0);
    float secondaryFresnel = clamp(NdotV + 0.7, 0.0, 1.0);
    float tertiaryFresnel  = clamp((1.0 - pow(NdotVpos, 0.01)) * 60.0, 0.0, 1.0);
    float diffuse = smoothstep(-WraparoundAmount, WraparoundAmount, NdotL) * uMaterial._PlanetSunLightEnabled;

    float atmosphereOpacity = mix((primaryFresnel + tertiaryFresnel) * diffuse, SunsetBrightness, sunsetPercent) * secondaryFresnel;

    float alpha = mix(atmosphereOpacity, CloudOpacity, cloudSample) * uMaterial._FinalOpacityMult;

    vec3 applyShadow     = mix(atmosphere, ShadowTint, clamp(shadow - cloudSample, 0.0, 1.0));
    vec3 applyCloud      = mix(applyShadow, CloudColor, cloudSample);
    vec3 applyNightCloud = mix(applyCloud, mix(NightColor, NightCloudColor, cloudSample), 1.0 - diffuse);

    outColor = vec4(applyNightCloud, alpha);
}
