#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;      // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _Color2;     // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _MinOpacity;  // @default = 0.0
    float _MaxOpacity;  // @default = 1.0
    float _FlickerSpeed; // @default = 1.0
    float _GameSeconds;  // global, set by TickManager
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _NoiseTex;

void main()
{
    vec2 uv = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    float noise = texture(_NoiseTex, vec2(u._GameSeconds * u._FlickerSpeed)).r;
    float opacity = mix(u._MinOpacity, u._MaxOpacity, noise);
    vec4 col = texture(_MainTex, uv);
    col.a *= opacity;
    outColor = clamp(col * u._Color * vColor * u._Color2, 0.0, 1.0);
}
