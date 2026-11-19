#version 450

// Final composite: draws the graded scene into the swapchain. No anti-aliasing, stock never attaches one.

layout(location = 0) in vec2 vUv;
layout(location = 0) out vec4 outColor;

layout(set = 0, binding = 0) uniform sampler2D _MainTex;

layout(set = 0, binding = 1) uniform Params { float saturation; } params;

void main()
{
    vec3 rgb = textureLod(_MainTex, vUv, 0.0).rgb;
    float lum = dot(rgb, vec3(0.22, 0.707, 0.071)); // unity's gamma space Luminance, as ColorCorrectionCurvesSimple uses
    outColor = vec4(clamp(mix(vec3(lum), rgb, params.saturation), 0.0, 1.0), 1.0);
}
