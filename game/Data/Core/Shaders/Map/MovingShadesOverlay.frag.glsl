#version 450

layout(location = 4) in vec3 vPosLocal;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Time; // global, MapRenderFeature
} u;

void main()
{
    vec2 uv = vPosLocal.xz / 40.0;
    float time = u._Time.y * 0.1;
    vec2 distortion = vec2(cos(uv.y * 10.0 + time), sin(-uv.x * 10.0 + time * 2.0));
    distortion *= vec2(sin(-uv.y * 10.0 + time * 2.0), sin(uv.x * 10.0 + time * 2.0));
    distortion += vec2(0.3 * cos(uv.x * 5.0), cos(uv.x * 5.0));
    uv += distortion;
    float d = uv.x - uv.y;
    d *= 5.0;
    d = sin(d);
    d = d * 0.5 + 0.5;
    d = 1.0 - d;
    d = smoothstep(0.1, 0.65, d * 0.2);
    outColor = vec4(d, d, d, 0.01);
}
