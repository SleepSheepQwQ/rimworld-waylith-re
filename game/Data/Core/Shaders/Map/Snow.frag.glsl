#version 450

layout(location = 0) in vec4 vColor; // r = pollution amount, a = snow depth (opacity)
layout(location = 1) in vec2 vUv;    // object-space XZ

layout(location = 0) out vec4 outColor;

layout(set = 2, binding = 0) uniform MaterialUniforms
{
    vec4 _Color; // @color @default = vec4(1.0, 1.0, 1.0, 1.0)
} uMaterial;

layout(set = 2, binding = 1) uniform sampler2D _MainTex;
layout(set = 2, binding = 2) uniform sampler2D _PollutedTex;
layout(set = 2, binding = 3) uniform sampler2D _MacroTex;
layout(set = 2, binding = 4) uniform sampler2D _AlphaAddTex;

void main()
{
    vec2 uv = vUv / 16.0; // 64px per square on a 1024 map
    uv.y = -uv.y; 

    vec4 mainTexCol = texture(_MainTex, uv);
    vec4 pollutionTexCol = texture(_PollutedTex, uv / 2.0);
    float pollutionOpacity = vColor.r;

    vec4 col = vColor;
    col.r = 1.0;

    vec4 macroTexCol = texture(_MacroTex, uv / 4.0);

    float alphaOffset = texture(_AlphaAddTex, uv / 2.0).r * 0.333
                      + texture(_AlphaAddTex, uv).g * 0.333
                      + texture(_AlphaAddTex, uv * 8.0).b * 0.333;

    vec4 val = mainTexCol * macroTexCol * col;

    // Stepped opacity so the snow edge reads as a crisp line rather than a soft gradient.
    float interceptVal = alphaOffset * 0.4 + val.a * 0.6;
    float aMul = 0.6;
    if (interceptVal > 0.65)      aMul = 1.0;
    else if (interceptVal > 0.4)  aMul = 0.9;
    else if (interceptVal > 0.18) aMul = 0.75;
    val.a *= aMul;

    float pollution = min(pollutionOpacity * (1.0 + (1.0 - val.a * 0.5)), 1.0);
    val.rgb = mix(val.rgb, pollutionTexCol.rgb, pollution);

    outColor = val * uMaterial._Color;
}
