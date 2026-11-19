#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;         // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;    // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _NumFrames;    // @default = 1.0
    float _AgeSecs;      // @default = 0.0
    float _FramesPerSec; // @default = 1.0
    float _Loop;         // @default = 1.0
    float _Duration;     // @default = 1.0
} u;

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vColor = inColor;

    uint curFrame = uint(u._AgeSecs * u._FramesPerSec);
    if (u._Loop == 1.0)
        curFrame %= uint(u._NumFrames);
    else
        curFrame = uint(min(float(curFrame), u._NumFrames - 1.0));
    float frameWidth = 1.0 / u._NumFrames;
    vec2 texCoord = inUv0.xy * u._MainTex_ST.zw + u._MainTex_ST.xy;
    texCoord.x = texCoord.x * frameWidth + float(curFrame) * frameWidth;
    vUv = texCoord;
}
