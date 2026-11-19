#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _MainTex_ST; // @default = vec4(0.0, 0.0, 1.0, 1.0)
    vec4 _DrawPos;
    float _Progress;
    float _IsTakeoff;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _GrabGravship;

const float MIN_BUBBLE_BOUND = 0.0;
const float MED_BUBBLE_BOUND = 0.01;
const float MAX_BUBBLE_BOUND = 0.2;
const float INTENSITY = 0.5;

void main()
{
    vec2 grabSize = vec2(textureSize(_GrabGravship, 0));
    float aspect = grabSize.x / grabSize.y;
    vec2 aspect2 = vec2(1.0, 1.0 / aspect);

    vec2 uv = vUv * u._MainTex_ST.zw + u._MainTex_ST.xy;
    vec2 diff = aspect2 * (u._DrawPos.xy - uv);
    float dist = length(diff);
    float intensity = dist > MED_BUBBLE_BOUND
        ? 1.0 - smoothstep(MED_BUBBLE_BOUND, MAX_BUBBLE_BOUND, dist)
        : smoothstep(MIN_BUBBLE_BOUND, MED_BUBBLE_BOUND, dist);
    intensity *= u._IsTakeoff > 0.5 ? smoothstep(0.0, 0.3, u._Progress) : 1.0 - smoothstep(0.5, 0.8, u._Progress);

    vec2 grabUv = gl_FragCoord.xy / grabSize + vec2(diff.x, -diff.y) * intensity * INTENSITY;
    float inBounds = step(0.0, grabUv.x) * step(grabUv.x, 1.0) * step(0.0, grabUv.y) * step(grabUv.y, 1.0);
    vec3 col = mix(vec3(0.1), texture(_GrabGravship, grabUv).rgb, inBounds);
    outColor = vec4(col, 1.0);
}
