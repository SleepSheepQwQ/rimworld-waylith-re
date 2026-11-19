#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _MainTex_ST;   // @default = vec4(0.0, 0.0, 1.0, 1.0)
    float _ChangeSpeed; // @default = 1.0
    float _AgeSecs;     // @default = 0.0
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec3 hue(float h)
{
    float r = abs(h * 6.0 - 3.0) - 1.0;
    float g = 2.0 - abs(h * 6.0 - 2.0);
    float b = 2.0 - abs(h * 6.0 - 4.0);
    return clamp(vec3(r, g, b), 0.0, 1.0);
}

vec3 hsvToRgb(vec3 hsv)
{
    return ((hue(hsv.x) - 1.0) * hsv.y + 1.0) * hsv.z;
}

vec3 rgbToHsv(vec3 rgb)
{
    vec3 hsv = vec3(0.0);
    hsv.z = max(rgb.r, max(rgb.g, rgb.b));
    float m = min(rgb.r, min(rgb.g, rgb.b));
    float c = hsv.z - m;
    if (c != 0.0)
    {
        hsv.y = c / hsv.z;
        vec3 delta = (hsv.z - rgb) / c;
        delta.rgb -= delta.brg;
        delta.rg += vec2(2.0, 4.0);
        if (rgb.r >= hsv.z)
            hsv.x = delta.b;
        else if (rgb.g >= hsv.z)
            hsv.x = delta.r;
        else
            hsv.x = delta.g;
        hsv.x = fract(hsv.x / 6.0);
    }
    return hsv;
}

void main()
{
    vec4 f = texture(_MainTex, vUv * u._MainTex_ST.zw + u._MainTex_ST.xy);
    vec3 hsv = rgbToHsv(f.rgb);
    float transformedAge = fract(u._AgeSecs * u._ChangeSpeed) * 2.0;
    hsv.r = mod(hsv.r + transformedAge, 1.57);
    f.rgb = hsvToRgb(hsv);
    outColor = f;
}
