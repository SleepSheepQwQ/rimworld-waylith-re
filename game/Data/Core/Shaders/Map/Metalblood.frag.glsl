#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _WorldSpaceCameraPos; // global, MapRenderFeature
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _SpecularNoiseTex; // global

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec4 tex = texture(_MainTex, flipV(vUv));
    if (tex.a < 0.1)
        discard;
    vec4 grey = vec4(0.4, 0.4, 0.4, 1.0);
    vec4 result = tex * grey;
    float srcPixelLum = dot(tex.rgb, vec3(0.2126, 0.7152, 0.0722));
    if (srcPixelLum < 0.2)
    {
        outColor = result;
        return;
    }
    vec2 offset = (u._WorldSpaceCameraPos.xz / 150.0 - vWorldPos.xz / 4.0) * 2.0;
    vec4 noise = texture(_SpecularNoiseTex, flipV(vUv * 2.0 + offset));
    outColor = result + noise;
}
