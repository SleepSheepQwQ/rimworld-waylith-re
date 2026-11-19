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
    float _AgeSecs;        // @default = 0.0
    float _Intensity;      // @default = 1.0
    float _FramesPerSec;   // @default = 1.0
    float _NumFrames;      // @default = 1.0
    float _RandomPerObjectOffsetRandom; // global @default = 0.0
} u;

layout(set = 2, binding = 3) uniform sampler2D _AgeScalarNoise;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vColor = inColor;

    float ageNoiseUVOffset = mod(abs(u._RandomPerObjectOffsetRandom / 10000.0), 1.0);
    float ageNoise = textureLod(_AgeScalarNoise, flipV(vec2(ageNoiseUVOffset, 0.0)), 0.0).r;
    float noisedAge = u._AgeSecs * ageNoise;

    float animationProgress = min(noisedAge * u._FramesPerSec, u._NumFrames - 1.0);
    int curFrame = int(uint(animationProgress) % uint(u._NumFrames));

    float frameWidth = 1.0 / u._NumFrames;
    float offset = float(curFrame) * frameWidth;

    vec2 texCoord = inUv0.xy * u._MainTex_ST.zw + u._MainTex_ST.xy;
    texCoord.x = texCoord.x * frameWidth + offset;
    vUv = texCoord;
}
