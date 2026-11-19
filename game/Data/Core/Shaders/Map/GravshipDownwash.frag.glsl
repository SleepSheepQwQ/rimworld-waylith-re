#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;        // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _DrawPos;
    vec4 _ScreenParams; // global, published by MapRenderFeature
    float _Progress;
    float _IsTakeoff;
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

void main()
{
    const float PI = 3.141592;

    float aspect = u._ScreenParams.x / u._ScreenParams.y;
    vec2 aspect2 = vec2(1.0, 1.0 / aspect * 1.2);
    vec2 uv = aspect2 * (vUv - u._DrawPos.xy);
    float angle = atan(uv.y, uv.x);
    float dist = length(uv);
    vec2 uvCircle = vec2(angle, dist);

    float opacityProgress = mix(1.0 - u._Progress, u._Progress, u._IsTakeoff);
    float fadeIn = smoothstep(0.0, 0.1, opacityProgress);

    vec2 noise = 0.5 - texture(_MainTex, flipV(uvCircle * 0.2)).xy;
    vec4 col = texture(_MainTex, flipV(noise / max(0.1, dist) * 0.1 + vec2(angle, dist * 2.0 - 3.33 * u._Progress * (1.5 - sqrt(u._Progress * 0.5)) * 2.0)));
    col *= u._Color;

    col.a *= dist * clamp(1.0 - opacityProgress * opacityProgress, 0.0, 1.0) * fadeIn;
    col.a *= smoothstep(-PI, -PI + 0.05, angle) * (1.0 - smoothstep(PI - 0.05, PI, angle));

    outColor = col;
}
