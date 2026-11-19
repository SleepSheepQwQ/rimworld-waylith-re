#version 450

// Sprite-sheet dual-frame blend
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vTexCoordCur;
layout(location = 3) out vec2 vTexCoordNext;
layout(location = 4) out float vScale;
layout(location = 5) out float vFrameProgress;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                  // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;             // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _DistortionScrollSpeed;  // @default = vec4(0.45, 0.45, 0.0, 0.0)
    float _AgeSecs;                 // @default = 0.0
    float _NumFrames;               // @default = 1.0
    float _FramesPerSec;            // @default = 1.0
    float _Intensity;               // @default = 1.0
    float _DistortionScale;         // @default = 1.0
} u;

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vColor = inColor;
    vUv = inUv0.xy;

    float animationProgress = min(u._AgeSecs * u._FramesPerSec, u._NumFrames - 1.0);
    vFrameProgress = fract(animationProgress);

    int curFrame = int(animationProgress) % int(u._NumFrames);
    float frameWidth = 1.0 / u._NumFrames;
    float offset = float(curFrame) * frameWidth;

    vec2 texCoordCur = inUv0.xy * u._MainTex_ST.zw + u._MainTex_ST.xy;
    texCoordCur.x = inUv0.x * frameWidth + offset;
    vTexCoordCur = texCoordCur;

    if (float(curFrame) < (u._NumFrames - 1.0))
        vTexCoordNext = texCoordCur + vec2(frameWidth, 0.0);
    else
        vTexCoordNext = texCoordCur;

    vScale = length(push.modelMatrix[2].xyz);
}
