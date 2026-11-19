#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;            // @color @default = vec4(0.0, 0.0, 0.0, 1.0)
    vec4 _ScreenParams;     // global, published by MapRenderFeature
    float _StartOpacity;    // @default = 1.0
    float _EndOpacity;      // @default = 0.0
    float _StartSize;       // @default = 1.0
    float _EndSize;         // @default = 2.0
    float _Progress;
    float _IsTakeoff;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

vec4 texLodClipped(vec2 uv, float lod, vec2 texelSize)
{
    float maxDim = 1.0 / max(texelSize.x, texelSize.y);
    vec4 color = textureLod(_MainTex, flipV(uv), lod);

    float texelSizeLod = exp2(lod + 1.0) / maxDim;

    // fade starts 1 texel away from the edge
    float fadeStart = 1.0 - texelSizeLod;
    color.a *= (1.0 - smoothstep(fadeStart, 1.0, uv.x)) *
               (1.0 - smoothstep(fadeStart, 1.0, uv.y)) *
               smoothstep(0.0, texelSizeLod, uv.x) *
               smoothstep(0.0, texelSizeLod, uv.y);

    return color;
}

// https://github.com/Experience-Monks/glsl-fast-gaussian-blur
vec4 blur13(vec2 uv, vec2 resolution, vec2 direction, float lod, vec2 texelSize)
{
    vec4 color = vec4(0.0);
    resolution /= exp2(lod);
    vec2 off1 = vec2(1.411764705882353) * direction;
    vec2 off2 = vec2(3.2941176470588234) * direction;
    vec2 off3 = vec2(5.176470588235294) * direction;
    color += texLodClipped(uv, lod, texelSize) * 0.1964825501511404;
    color += texLodClipped(uv + off1 / resolution, lod, texelSize) * 0.2969069646728344;
    color += texLodClipped(uv - off1 / resolution, lod, texelSize) * 0.2969069646728344;
    color += texLodClipped(uv + off2 / resolution, lod, texelSize) * 0.09447039785044732;
    color += texLodClipped(uv - off2 / resolution, lod, texelSize) * 0.09447039785044732;
    color += texLodClipped(uv + off3 / resolution, lod, texelSize) * 0.010381362401148057;
    color += texLodClipped(uv - off3 / resolution, lod, texelSize) * 0.010381362401148057;
    return color;
}

void main()
{
    float height = u._Progress * u._Progress;
    vec2 uv = (vUv - 0.5) / mix(u._StartSize, u._EndSize, height) + 0.5;

    vec2 texelSize = 1.0 / vec2(textureSize(_MainTex, 0));
    vec2 pixelsPerWorldUnit = u._ScreenParams.xy / 256.0;
    float maxDim = 1.0 / max(texelSize.x, texelSize.y);
    float worldScaledDim = maxDim * max(pixelsPerWorldUnit.x, pixelsPerWorldUnit.y);
    float lod = max(0.0, log2(worldScaledDim) - log2(256.0));

    float shadowFactor = blur13(uv, vec2(textureSize(_MainTex, 0)), vec2(0.5), lod, texelSize).a;
    vec4 color = u._Color;
    color.a *= mix(u._StartOpacity, u._EndOpacity, u._Progress);
    color.a *= mix(1.0 - smoothstep(0.8, 1.0, u._Progress), 1.0 - smoothstep(0.6, 0.8, u._Progress), u._IsTakeoff);
    outColor = mix(vec4(0.0), color, shadowFactor);
}
