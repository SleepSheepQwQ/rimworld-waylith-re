#version 450

layout(location = 1) in vec2 vUv;

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color;          // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
    vec4 _Time;           // global, published by MapRenderFeature
    float _AgeSecs;
    float _AgeOffset;     // @default = 0.0
    float _innerRingSize; // @default = 0.6
    float _inTime;        // @default = 0.4
    float _solidTime;     // @default = 0.5
    float _outTime;       // @default = 0.3
} u;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;

vec2 flipV(vec2 c) { return vec2(c.x, 1.0 - c.y); }

float ageToIntensity()
{
    float age = u._AgeSecs + u._AgeOffset;

    if (age < u._inTime)
        return age / u._inTime;
    else if (age >= u._inTime + u._solidTime)
        return 1.0 - (age - (u._inTime + u._solidTime)) / u._outTime;

    return 1.0;
}

void main()
{
    vec2 centeredOffset = vUv * 2.0 - 1.0;
    vec2 texCoord = centeredOffset / u._innerRingSize;
    float distToCenter = length(centeredOffset);

    float opacity = ageToIntensity();

    float innerBorder = u._innerRingSize * opacity;
    if (distToCenter > innerBorder)
        discard;

    float distortion = sqrt(abs(1.0 - dot(texCoord, texCoord)));
    float extraBrightness = 1.0 + pow(1.0 - abs(innerBorder - distToCenter), 14.0) * 10.0;

    vec4 color = texture(_MainTex, flipV(texCoord / distortion * vec2(0.15, 0.4) + vec2(u._AgeSecs * 0.5 + u._Time.y * 0.02, 0.3))) * vec4(vec3(extraBrightness), 1.0);
    color.a = u._Color.a;

    outColor = color;
}
