#version 450
// Minimal stand-in for the ImGui quad vertex stage: the real renderer feeds the fragment stage
// vUv (the quad's texcoords) and vColor (the vertex colour) at locations 0 and 1, with the
// transform coming from push constants. The push-constant transform does not affect uv/colour, so
// a direct NDC quad is equivalent for testing the fragment behaviour.

layout(location = 0) in vec2 inPos;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec4 vColor;
layout(location = 1) out vec2 vUv;

void main()
{
    vColor = inColor;
    vUv = inUv;
    gl_Position = vec4(inPos, 0.0, 1.0);
}
