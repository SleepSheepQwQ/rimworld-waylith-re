#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

void main()
{
    vec4 srcPixel = texture(_MainTex, vec2(vUv.x, 1.0 - vUv.y));
    if (srcPixel.a < 0.5)
        discard;

    float gray = dot(srcPixel.rgb, vec3(0.2126, 0.7152, 0.0722));
    srcPixel.rgb = vec3(gray);

    vec4 dstPixel = srcPixel * uMaterial._Color * vColor;
    dstPixel.rgb *= (245.0 / 255.0); // body graphics cap at 245, correct back up
    outColor = dstPixel;
}
