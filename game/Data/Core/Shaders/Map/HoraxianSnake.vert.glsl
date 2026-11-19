#version 450

layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 1) out vec2 vUv;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;           // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _MainTex_ST;      // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _Color2;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _NoiseReadScale; // @default = 0.02
    float _AgeSecs;        // @default = 0.0
    float _RampTime;       // @default = 0.2
} u;

layout(set = 2, binding = 2) uniform sampler2D _RippleTex; // global

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    vec2 worldPos = (push.modelMatrix * vec4(inPosition, 1.0)).xz;
    float noise = textureLod(_RippleTex, flipV(worldPos * u._NoiseReadScale), 0.0).r * 2.0 - 1.0;
    float rampedAge = clamp(u._AgeSecs / u._RampTime, 0.0, 1.0);
    vec3 noiseOffset = vec3(-noise, 0.0, noise) * rampedAge;
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition + noiseOffset, 1.0);
    vUv = inUv0.xy;
}
