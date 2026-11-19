#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec2 _ScrollSpeed;   // @default = vec2(0.2, 0.2)
    float _SoftEdgeSize; // @default = 0.2
    float _TextureScale; // @default = 1.0
    float _GameSeconds;  // global, set by TickManager
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _ExtraTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    float distToCenter = pow(distance(vUv, vec2(0.5)) * 2.0, 2.0);
    if (distToCenter > 1.0)
        discard;
    float fade = max(smoothstep(1.0 - u._SoftEdgeSize, 1.0, distToCenter), 1.0 - u._Color.a);
    vec4 clouds = texture(_MainTex, flipV(vUv / u._TextureScale - u._GameSeconds * u._ScrollSpeed))
                * texture(_ExtraTex, flipV(vUv / u._TextureScale + u._GameSeconds * u._ScrollSpeed));
    outColor = mix(clouds, vec4(1.0), fade);
}
