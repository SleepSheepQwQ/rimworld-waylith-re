#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vRand;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;                    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _FlickerFrequency;          // @default = 1.0
    float _FlickerAmount;             // @default = 1.0
    float _InnerCircleSize;           // @default = 1.0
    float _InnerCircleShimmerAmount;  // @default = 0.1
    float _InnerCircleIntensity;      // @default = 0.2
    float _AgeSecs;                   // @default = 0.0
} u;

layout(set = 2, binding = 2) uniform sampler2D _RandomTex; // @default = black

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vColor = inColor;
    vUv = inUv0.xy;

    vRand = vec2(textureLod(_RandomTex, flipV(vec2(u._AgeSecs * u._FlickerFrequency + 0.5, 0.0)), 0.0).r,
                 textureLod(_RandomTex, flipV(vec2(u._AgeSecs * u._FlickerFrequency, 0.0)), 0.0).r);
}
