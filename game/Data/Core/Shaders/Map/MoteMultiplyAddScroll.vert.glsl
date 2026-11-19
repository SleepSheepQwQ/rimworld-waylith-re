#version 450

// Computes a pawn-relative "global" coordinate in world space so the multiply/detail layers scroll across the
// whole pawn rather than per-quad.
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vGlobalCoord;

layout(set = 0, binding = 0) uniform VertexUniforms { mat4 ViewProj; } uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;
    vec4 _pawnCenterWorld;
    vec4 _pawnDrawSizeWorld;
    vec4 _texAScrollSpeed;
    vec4 _texBScrollSpeed;
    vec4 _DetailScrollSpeed;
    vec4 _DistortionScrollSpeed;
    vec4 _DetailOffset;
    float _AgeSecs;
    float _detailIntensity;
    float _texAScale;
    float _texBScale;
    float _DetailScale;
    float _DetailDistortion;
    float _OutlineMultiplier;
} u;

void main()
{
    vec4 world = push.modelMatrix * vec4(inPosition, 1.0);
    gl_Position = uVertex.ViewProj * world;
    vColor = inColor;
    vUv = inUv0.xy;
    vGlobalCoord = (world.xz - u._pawnCenterWorld.xy + u._pawnDrawSizeWorld.xy / 2.0) / u._pawnDrawSizeWorld.xy;
}
