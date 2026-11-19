/* libstb.so built from the public-domain stb headers.
 *
 * Brutal.Stb P/Invokes the plain stb_image / stb_image_write entry points (stbi_load,
 * stbi_load_from_memory, stbi_load_16*, stbi_loadf*, stbi_is_hdr*, stbi_is_16_bit*,
 * stbi_write_{png,jpg,bmp,tga,hdr}, stbi_image_free, stbi_failure_reason, ...). The Android
 * bundle ships a prebuilt copy; on a Linux runner we compile the headers ourselves
 * (Ubuntu: apt-get install libstb-dev -> /usr/include/stb).
 *
 * This is what makes the PNG icons decode to real pixels in the headless run. The port's .dds
 * path needs its own native `gli` bridge instead, which is not part of this repository.
 */
#define STB_IMAGE_IMPLEMENTATION
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include <stb/stb_image.h>
#include <stb/stb_image_write.h>
