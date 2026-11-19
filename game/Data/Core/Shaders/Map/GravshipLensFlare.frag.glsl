#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Color2;        // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _DrawPos;
    vec4 _ScreenParams;  // global, published by MapRenderFeature
    vec4 _Time;          // global, published by MapRenderFeature
    float _MinBrightness; // @default = 0.0
    float _MaxBrightness; // @default = 1.0
    float _FlickerSpeed;  // @default = 1.0
    float _LensFlareSize; // @default = 1.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _NoiseTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec4 texClip(vec2 uv)
{
    return (uv.x < 0.0 || uv.y < 0.0 || uv.x > 1.0 || uv.y > 1.0) ? vec4(0.0) : texture(_MainTex, flipV(uv));
}

void main()
{
    float aspect = u._ScreenParams.x / u._ScreenParams.y;
    vec2 aspect2 = vec2(1.0, 1.0 / aspect);
    float noise = texture(_NoiseTex, flipV(vec2(u._Time.y * u._FlickerSpeed))).r;
    float brightness = mix(u._MinBrightness, u._MaxBrightness, noise);
    outColor = texClip(aspect2 * (u._DrawPos.xy - vUv) / u._LensFlareSize + 0.5) * u._Color * u._Color2 * brightness;
}
