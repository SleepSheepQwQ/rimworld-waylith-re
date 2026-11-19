#version 450

// Spins a second set of texcoords around the quad centre so the scan sweep rotates over time.
layout(location = 0) in vec3 inPosition; // @vertex Position required
layout(location = 2) in vec4 inColor; // @vertex Color optional @default = vec4(1.0, 1.0, 1.0, 1.0)
layout(location = 3) in vec4 inUv0; // @vertex TexCoord0 optional @default = vec4(0.0, 0.0, 0.0, 0.0)

layout(push_constant) uniform Push { mat4 modelMatrix; } push;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;
layout(location = 2) out vec2 vTexCoordRotated;

layout(set = 0, binding = 0) uniform VertexUniforms
{
    mat4 ViewProj;
} uVertex;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;
    float _GameSeconds;
    float _smokeScale;
    float _smokeScrollSpeed;
    float _rotationSpeed;
    float _smokeAmount;
} u;

void main()
{
    gl_Position = uVertex.ViewProj * push.modelMatrix * vec4(inPosition, 1.0);
    vColor = inColor;
    vUv = inUv0.xy;

    float angle = u._GameSeconds * u._rotationSpeed * 13.0;
    float s = sin(angle);
    float c = cos(angle);

    vec2 pivot = vec2(0.5, 0.5);
    vec2 rc = inUv0.xy - pivot;
    vTexCoordRotated = vec2(c * rc.x - s * rc.y, s * rc.x + c * rc.y) + pivot;
}
