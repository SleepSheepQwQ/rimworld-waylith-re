#version 450

layout(local_size_x = 8, local_size_y = 8) in;

layout(set = 0, binding = 0) uniform sampler2D uLuma;
layout(set = 0, binding = 1) uniform sampler2D uChroma;
layout(set = 0, binding = 2, rgba8) uniform writeonly image2D uOutput;

layout(push_constant) uniform Push
{
    vec4 rowR;
    vec4 rowG;
    vec4 rowB;
    ivec2 cropOffset;
    ivec2 size;
} pc;

void main()
{
    ivec2 p = ivec2(gl_GlobalInvocationID.xy);
    if (any(greaterThanEqual(p, pc.size)))
        return;

    ivec2 src = p + pc.cropOffset;
    float y = texelFetch(uLuma, src, 0).r;

    // h264 default chroma siting (left, vertically centred), clamped so padding outside the crop never bleeds in
    vec2 c = vec2(src) * 0.5 + vec2(0.5, 0.25);
    vec2 lo = vec2(pc.cropOffset / 2) + 0.5;
    vec2 hi = vec2((pc.cropOffset + pc.size + 1) / 2) - 0.5;
    vec2 cbcr = texture(uChroma, clamp(c, lo, hi) / vec2(textureSize(uChroma, 0))).rg;

    vec4 v = vec4(y, cbcr, 1.0);
    imageStore(uOutput, p, vec4(dot(pc.rowR, v), dot(pc.rowG, v), dot(pc.rowB, v), 1.0));
}
