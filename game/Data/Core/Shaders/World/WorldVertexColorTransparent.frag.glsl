#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec3 vUv;

layout(location = 0) out vec4 outColor;

void main()
{
    float a = vColor.a;
    const float cutoff = 0.02;
    if (vUv.x > cutoff && vUv.z > cutoff)
        a = 0.0;
    if (a <= 0.0)
        discard; // AlphaTest Greater 0
    outColor = vec4(vColor.rgb, a);
}
