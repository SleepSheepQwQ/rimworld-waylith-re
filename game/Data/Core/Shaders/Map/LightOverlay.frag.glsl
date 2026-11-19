#version 450

layout(location = 0) in vec4 vColor;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0) - sky light colour
} uMaterial;

void main()
{
    // Sky contribution, scaled by how much sky covers this cell (vertex alpha), plus the glow-grid colour.
    float skyCover = vColor.a;
    vec4 skyContribution = uMaterial._Color * (1.0 - skyCover);
    vec4 color = skyContribution + vColor;

    // Special clamping to avoid colour distortion. colours may go slightly overbright (glow).
    const float clampExtraFactor = 0.5;
    float clampBase = 1.0 + clampExtraFactor - clampExtraFactor * min(skyContribution.r, skyContribution.b);
    if (color.r > clampBase) color *= clampBase / color.r;
    if (color.g > clampBase) color *= clampBase / color.g;
    if (color.b > clampBase) color *= clampBase / color.b;

    outColor = color * 0.5; // blend is 2 * dst * src, so the overbright range survives the unorm clamp
}
