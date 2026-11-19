#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

const vec3 SunColor = vec3(1.0, 0.847, 0.0);

void main()
{
    // Radial falloff from the quad centre.
    float val = max(1.0 - distance(vUv, vec2(0.5, 0.5)) * 2.0, 0.0);

    vec3 color = SunColor;
    color *= pow(val, 4.0);   // sharp bright core
    color += val;             // soft outer glow
    color = min(color, vec3(1.0));
    color *= val;             // final softening

    outColor = vec4(color, 1.0);
}
