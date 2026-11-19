#version 450

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 1) uniform sampler2D _GrabScreen;

void main()
{
    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabScreen, 0));
    outColor = vec4(texture(_GrabScreen, grabUv).rgb, 1.0);
}
