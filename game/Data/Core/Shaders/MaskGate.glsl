// waylith does a colour bleed into fully transparent texels to fix cutout fringes, which can mess with mask textures with partial alpha on empty texels
// this is the best effort fix for that
float MaskGate(vec4 mask)
{
    return clamp(mask.a * 255.0, 0.0, 1.0);
}
