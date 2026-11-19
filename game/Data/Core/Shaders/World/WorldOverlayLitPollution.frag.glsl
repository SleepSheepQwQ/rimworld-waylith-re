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
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 sphereCoordsToUV(vec3 normal)
{
    float absX = abs(normal.x);
    float absY = abs(normal.y);
    float absZ = abs(normal.z);
    float maxAxis;
    float uc;
    float vc;

    if (absX >= absY && absX >= absZ)
    {
        maxAxis = absX;
        vc = normal.y;
        uc = normal.x > 0.0 ? -normal.z : normal.z;
    }
    else if (absY >= absX && absY >= absZ)
    {
        maxAxis = absY;
        uc = normal.x;
        vc = normal.y > 0.0 ? -normal.z : normal.z;
    }
    else
    {
        maxAxis = absZ;
        vc = normal.y;
        uc = normal.z > 0.0 ? normal.x : -normal.x;
    }

    return vec2(0.5 * (uc / maxAxis + 1.0), 0.5 * (vc / maxAxis + 1.0));
}

const vec3 NightColor = vec3(0.482, 0.603, 0.682);
const vec3 DayColor   = vec3(1.0, 1.0, 1.0);
const float DayNightStart = 0.45;
const float DayNightEnd   = 0.55;

vec3 sunDiffuseLight(vec3 normal, vec3 lightDir)
{
    if (dot(lightDir, lightDir) < 1e-6)
        return DayColor;
    float light = 1.0 - (dot(normal, normalize(lightDir)) * 0.5 + 0.5);
    float t = (light - DayNightStart) / (DayNightEnd - DayNightStart);
    return mix(NightColor, DayColor, clamp(t, 0.0, 1.0));
}

void main()
{
    vec3 normal = normalize(vWorldPos);
    vec4 fin = texture(_MainTex, sphereCoordsToUV(normal) * vec2(16.0, -16.0));
    fin *= vColor * uMaterial._Color;
    fin.rgb *= sunDiffuseLight(normal, uFragment.SunDir.xyz);
    outColor = fin;
}
