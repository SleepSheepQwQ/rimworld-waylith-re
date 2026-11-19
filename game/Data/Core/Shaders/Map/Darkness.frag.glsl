#version 450

layout(location = 0) in vec4 vColor;
layout(location = 1) in vec3 vWorldPos;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;    // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _ColorTwo; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    float _AgeSecsPausable;
} u;

layout(set = 2, binding = 1) uniform sampler2D _GrabDarkness;

vec4 mod289(vec4 x)
{
    return x - floor(x * (1.0 / 289.0)) * 289.0;
}

vec4 perm(vec4 x)
{
    return mod289(((x * 34.0) + 1.0) * x);
}

float noise(vec3 p)
{
    vec3 a = floor(p);
    vec3 d = p - a;
    d = d * d * (3.0 - 2.0 * d);

    vec4 b = a.xxyy + vec4(0.0, 1.0, 0.0, 1.0);
    vec4 k1 = perm(b.xyxy);
    vec4 k2 = perm(k1.xyxy + b.zzww);

    vec4 c = k2 + a.zzzz;
    vec4 k3 = perm(c);
    vec4 k4 = perm(c + 1.0);

    vec4 o1 = fract(k3 * (1.0 / 41.0));
    vec4 o2 = fract(k4 * (1.0 / 41.0));

    vec4 o3 = o2 * d.z + o1 * (1.0 - d.z);
    vec2 o4 = o3.yw * d.x + o3.xz * (1.0 - d.x);

    return o4.y * d.y + o4.x * (1.0 - d.y);
}

void main()
{
    vec4 color = vColor * u._Color * u._ColorTwo;

    vec2 grabUv = gl_FragCoord.xy / vec2(textureSize(_GrabDarkness, 0));
    vec4 grabTexCol = texture(_GrabDarkness, grabUv);

    float lum = clamp(dot(grabTexCol.rgb, vec3(0.299, 0.587, 0.114)), 0.0, 1.0);
    vec4 output_;
    output_.rgb = mix(grabTexCol.rgb, vec3(lum), color.a);

    float off = u._AgeSecsPausable * 0.01;

    float nx = vWorldPos.x * sin(off) + vWorldPos.z * cos(off);
    float ny = vWorldPos.z * sin(-off) + vWorldPos.x * cos(-off);
    float n = noise(vec3(nx, ny, nx + ny + u._AgeSecsPausable * 0.8));

    const float fact = 0.1;
    float nx2 = vWorldPos.x * fact * sin(off) + vWorldPos.z * fact * cos(off);
    float ny2 = vWorldPos.z * fact * sin(-off) + vWorldPos.x * fact * cos(-off);
    float n2 = noise(vec3(nx2, ny2, nx2 + ny2 + u._AgeSecsPausable * 2.0)) * 2.0;
    output_.a = min(color.a + pow(color.a, 5.0) * n * n2, 0.85);

    outColor = output_ * color;
}
