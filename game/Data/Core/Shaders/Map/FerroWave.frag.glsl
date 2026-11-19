#version 450

layout(location = 1) in vec2 vUv;
layout(location = 2) in vec2 vObjectWorld;
layout(location = 3) in vec2 vVertexWorld;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _CastVect;   // global, map sun light direction (SkyManager)
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec2 lightDir = normalize(u._CastVect.xz);
    vec4 texColor = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy);
    if (texColor.a < 0.5)
        discard;
    vec2 dirToCenter = normalize(vVertexWorld - vObjectWorld);
    float lightDot = (dot(dirToCenter, lightDir) + 1.0) * 0.5 * 1.2 - 0.2; // Remap(-1..1 -> -0.2..1)
    texColor.rgb *= lightDot;
    outColor = texColor * u._Color;
}
