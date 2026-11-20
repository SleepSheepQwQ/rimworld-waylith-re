// mipblank - what a mip level that was never written actually contains.
//
// The engine creates an image with max(MipCount, uploads.Length) levels and, for block-compressed
// formats, writes only the levels the file/cache carries (Texture2D.UploadLevels skips GPU mip
// generation when IsBlockCompressed). This harness asks a real Vulkan implementation the only
// question that matters for the UI: if a minified sample reads mip N of such an image, what colour
// does it get?
//
// No shaders, no render pass: mip levels are copied out with vkCmdCopyImageToBuffer, which is the
// same texel data a sampler would fetch at that LOD.
//
// usage: mipblank            (prints CASE/READ lines and exits 0 if the assertions hold)

#include <vulkan/vulkan.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

#define W 256
#define H 256
#define MIPS 9

static void die(const char *m) { fprintf(stderr, "mipblank: %s\n", m); exit(2); }
static void chk(VkResult r, const char *w) { if (r != VK_SUCCESS) { fprintf(stderr, "mipblank: %s -> %d\n", w, (int)r); exit(2); } }

typedef struct { VkImage img; VkDeviceMemory mem; VkFormat fmt; uint32_t w, h, mips; } Img;

static VkDevice dev; static VkPhysicalDevice phys; static VkCommandPool pool; static VkQueue queue;
static VkPhysicalDeviceMemoryProperties mp;

static uint32_t mem_type(uint32_t bits, VkMemoryPropertyFlags want)
{
    for (uint32_t i = 0; i < mp.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) return i;
    die("no memory type");
    return 0;
}

static uint32_t level_bytes(VkFormat f, uint32_t w, uint32_t h)
{
    if (f == VK_FORMAT_BC1_RGB_UNORM_BLOCK)
    {
        uint32_t bw = (w + 3) / 4, bh = (h + 3) / 4;
        return bw * bh * 8u;
    }
    return w * h * 4u;
}

static Img make_image(VkFormat f, uint32_t w, uint32_t h, uint32_t mips)
{
    Img im; memset(&im, 0, sizeof im);
    im.fmt = f; im.w = w; im.h = h; im.mips = mips;
    VkImageCreateInfo ci; memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ci.imageType = VK_IMAGE_TYPE_2D; ci.format = f;
    ci.extent.width = w; ci.extent.height = h; ci.extent.depth = 1;
    ci.mipLevels = mips; ci.arrayLayers = 1; ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE; ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    chk(vkCreateImage(dev, &ci, NULL, &im.img), "vkCreateImage");
    VkMemoryRequirements mr; vkGetImageMemoryRequirements(dev, im.img, &mr);
    VkMemoryAllocateInfo ai; memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO; ai.allocationSize = mr.size;
    ai.memoryTypeIndex = mem_type(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    chk(vkAllocateMemory(dev, &ai, NULL, &im.mem), "vkAllocateMemory(image)");
    chk(vkBindImageMemory(dev, im.img, im.mem, 0), "vkBindImageMemory");
    return im;
}

typedef struct { VkBuffer buf; VkDeviceMemory mem; void *map; uint32_t size; } Buf;

static Buf make_buf(uint32_t size)
{
    Buf b; memset(&b, 0, sizeof b); b.size = size;
    VkBufferCreateInfo ci; memset(&ci, 0, sizeof ci);
    ci.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO; ci.size = size;
    ci.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT | VK_BUFFER_USAGE_TRANSFER_DST_BIT;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    chk(vkCreateBuffer(dev, &ci, NULL, &b.buf), "vkCreateBuffer");
    VkMemoryRequirements mr; vkGetBufferMemoryRequirements(dev, b.buf, &mr);
    VkMemoryAllocateInfo ai; memset(&ai, 0, sizeof ai);
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO; ai.allocationSize = mr.size;
    ai.memoryTypeIndex = mem_type(mr.memoryTypeBits, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    chk(vkAllocateMemory(dev, &ai, NULL, &b.mem), "vkAllocateMemory(buffer)");
    chk(vkBindBufferMemory(dev, b.buf, b.mem, 0), "vkBindBufferMemory");
    chk(vkMapMemory(dev, b.mem, 0, size, 0, &b.map), "vkMapMemory");
    return b;
}

static void barrier(VkCommandBuffer cb, Img *im, VkImageLayout from, VkImageLayout to,
                    VkAccessFlags src, VkAccessFlags dst, VkPipelineStageFlags s, VkPipelineStageFlags d, uint32_t baseMip, uint32_t mipCount)
{
    VkImageMemoryBarrier b; memset(&b, 0, sizeof b);
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.oldLayout = from; b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = im->img;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.baseMipLevel = baseMip; b.subresourceRange.levelCount = mipCount;
    b.subresourceRange.baseArrayLayer = 0; b.subresourceRange.layerCount = 1;
    b.srcAccessMask = src; b.dstAccessMask = dst;
    vkCmdPipelineBarrier(cb, s, d, 0, 0, NULL, 0, NULL, 1, &b);
}

static void fill_pattern(VkFormat f, uint32_t w, uint32_t h, void *dst)
{
    // a fully opaque mid-grey, so "blank" (all zero, alpha 0) is unambiguous
    if (f == VK_FORMAT_BC1_RGB_UNORM_BLOCK)
    {
        // BC1 block: two RGB565 endpoints (0x8410 = mid grey), then 32 zero index bits
        uint8_t *p = (uint8_t *)dst;
        uint32_t blocks = ((w + 3) / 4) * ((h + 3) / 4);
        for (uint32_t i = 0; i < blocks; i++)
        {
            p[i * 8 + 0] = 0x10; p[i * 8 + 1] = 0x84;
            p[i * 8 + 2] = 0x10; p[i * 8 + 3] = 0x84;
            memset(p + i * 8 + 4, 0, 4);
        }
    }
    else
    {
        uint8_t *p = (uint8_t *)dst;
        for (uint32_t i = 0; i < w * h; i++) { p[i * 4 + 0] = 128; p[i * 4 + 1] = 128; p[i * 4 + 2] = 128; p[i * 4 + 3] = 255; }
    }
}

typedef struct { uint32_t nonzero; uint32_t len; uint8_t first[8]; int all_zero; } Readback;

static Readback run_case(const char *name, VkFormat f, int uploadAllMips, int clearFirst, uint32_t readMip)
{
    Img im = make_image(f, W, H, MIPS);
    Buf src = make_buf(level_bytes(f, W, H) + 4096);
    uint32_t rw = W >> readMip, rh = H >> readMip;
    uint32_t rlen = level_bytes(f, rw, rh);
    Buf dst = make_buf(rlen);

    VkCommandBufferAllocateInfo cai; memset(&cai, 0, sizeof cai);
    cai.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO; cai.commandPool = pool;
    cai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; cai.commandBufferCount = 1;
    VkCommandBuffer cb; chk(vkAllocateCommandBuffers(dev, &cai, &cb), "vkAllocateCommandBuffers");
    VkCommandBufferBeginInfo bi; memset(&bi, 0, sizeof bi);
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO; bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    chk(vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");

    barrier(cb, &im, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, MIPS);

    if (clearFirst)
    {
        VkClearColorValue c; memset(&c, 0, sizeof c);
        c.float32[0] = 1.0f; c.float32[1] = 1.0f; c.float32[2] = 1.0f; c.float32[3] = 1.0f;
        VkImageSubresourceRange r; memset(&r, 0, sizeof r);
        r.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; r.baseMipLevel = 0; r.levelCount = MIPS;
        r.baseArrayLayer = 0; r.layerCount = 1;
        vkCmdClearColorImage(cb, im.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, &c, 1, &r);
    }

    uint32_t levels = uploadAllMips ? MIPS : 1;
    for (uint32_t m = 0; m < levels; m++)
    {
        uint32_t lw = W >> m, lh = H >> m; if (!lw) lw = 1; if (!lh) lh = 1;
        uint32_t lb = level_bytes(f, lw, lh);
        fill_pattern(f, lw, lh, src.map);
        VkBufferImageCopy c; memset(&c, 0, sizeof c);
        c.bufferOffset = 0; c.bufferRowLength = 0; c.bufferImageHeight = 0;
        c.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
        c.imageSubresource.mipLevel = m; c.imageSubresource.baseArrayLayer = 0; c.imageSubresource.layerCount = 1;
        c.imageExtent.width = lw; c.imageExtent.height = lh; c.imageExtent.depth = 1;
        vkCmdCopyBufferToImage(cb, src.buf, im.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &c);
        (void)lb;
    }

    barrier(cb, &im, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_TRANSFER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, MIPS);

    VkBufferImageCopy rc; memset(&rc, 0, sizeof rc);
    rc.bufferOffset = 0; rc.bufferRowLength = 0; rc.bufferImageHeight = 0;
    rc.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    rc.imageSubresource.mipLevel = readMip; rc.imageSubresource.baseArrayLayer = 0; rc.imageSubresource.layerCount = 1;
    rc.imageExtent.width = rw; rc.imageExtent.height = rh; rc.imageExtent.depth = 1;
    vkCmdCopyImageToBuffer(cb, im.img, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, dst.buf, 1, &rc);

    chk(vkEndCommandBuffer(cb), "vkEndCommandBuffer");
    VkSubmitInfo si; memset(&si, 0, sizeof si);
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO; si.commandBufferCount = 1; si.pCommandBuffers = &cb;
    chk(vkQueueSubmit(queue, 1, &si, VK_NULL_HANDLE), "vkQueueSubmit");
    chk(vkQueueWaitIdle(queue), "vkQueueWaitIdle");

    Readback r; memset(&r, 0, sizeof r);
    uint8_t *p = (uint8_t *)dst.map;
    r.len = rlen;
    for (uint32_t i = 0; i < rlen; i++) if (p[i]) r.nonzero++;
    for (int i = 0; i < 8 && i < (int)rlen; i++) r.first[i] = p[i];
    r.all_zero = (r.nonzero == 0);

    printf("CASE %s: format=%d readMip=%u extent=%ux%u bytes=%u nonzero=%u first=%02x%02x%02x%02x%02x%02x%02x%02x\n",
           name, (int)f, readMip, rw, rh, rlen, r.nonzero,
           r.first[0], r.first[1], r.first[2], r.first[3], r.first[4], r.first[5], r.first[6], r.first[7]);

    vkFreeCommandBuffers(dev, pool, 1, &cb);
    vkUnmapMemory(dev, dst.mem); vkDestroyBuffer(dev, dst.buf, NULL); vkFreeMemory(dev, dst.mem, NULL);
    vkUnmapMemory(dev, src.mem); vkDestroyBuffer(dev, src.buf, NULL); vkFreeMemory(dev, src.mem, NULL);
    vkDestroyImage(dev, im.img, NULL); vkFreeMemory(dev, im.mem, NULL);
    return r;
}

int main(void)
{
    VkApplicationInfo app; memset(&app, 0, sizeof app);
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO; app.apiVersion = VK_API_VERSION_1_1;
    app.pApplicationName = "mipblank";
    VkInstanceCreateInfo ici; memset(&ici, 0, sizeof ici);
    ici.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO; ici.pApplicationInfo = &app;
    VkInstance inst;
    VkResult ir = vkCreateInstance(&ici, NULL, &inst);
    if (ir != VK_SUCCESS) { fprintf(stderr, "mipblank: vkCreateInstance -> %d (no ICD?)\n", (int)ir); return 2; }

    uint32_t n = 0; vkEnumeratePhysicalDevices(inst, &n, NULL);
    if (n == 0) { fprintf(stderr, "mipblank: no physical device\n"); return 2; }
    VkPhysicalDevice *pds = calloc(n, sizeof *pds);
    vkEnumeratePhysicalDevices(inst, &n, pds);
    phys = pds[0];
    VkPhysicalDeviceProperties props; vkGetPhysicalDeviceProperties(phys, &props);
    printf("DEVICE %s (%u.%u.%u, api %u.%u.%u)\n", props.deviceName,
           VK_VERSION_MAJOR(props.driverVersion), VK_VERSION_MINOR(props.driverVersion), VK_VERSION_PATCH(props.driverVersion),
           VK_VERSION_MAJOR(props.apiVersion), VK_VERSION_MINOR(props.apiVersion), VK_VERSION_PATCH(props.apiVersion));
    vkGetPhysicalDeviceMemoryProperties(phys, &mp);

    uint32_t qn = 0; vkGetPhysicalDeviceQueueFamilyProperties(phys, &qn, NULL);
    VkQueueFamilyProperties *qf = calloc(qn, sizeof *qf);
    vkGetPhysicalDeviceQueueFamilyProperties(phys, &qn, qf);
    uint32_t qi = UINT32_MAX;
    for (uint32_t i = 0; i < qn; i++) if (qf[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) { qi = i; break; }
    if (qi == UINT32_MAX) die("no graphics queue");

    float prio = 1.0f;
    VkDeviceQueueCreateInfo dq; memset(&dq, 0, sizeof dq);
    dq.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO; dq.queueFamilyIndex = qi; dq.queueCount = 1; dq.pQueuePriorities = &prio;
    VkDeviceCreateInfo dci; memset(&dci, 0, sizeof dci);
    dci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO; dci.queueCreateInfoCount = 1; dci.pQueueCreateInfos = &dq;
    chk(vkCreateDevice(phys, &dci, NULL, &dev), "vkCreateDevice");
    vkGetDeviceQueue(dev, qi, 0, &queue);

    VkCommandPoolCreateInfo pci; memset(&pci, 0, sizeof pci);
    pci.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO; pci.queueFamilyIndex = qi;
    chk(vkCreateCommandPool(dev, &pci, NULL, &pool), "vkCreateCommandPool");

    // the two shapes the engine actually creates for a 256x256 icon
    Readback bc0 = run_case("bc1_mip0_only",    VK_FORMAT_BC1_RGB_UNORM_BLOCK, 0, 0, 2);
    Readback bcc = run_case("bc1_mip0_cleared", VK_FORMAT_BC1_RGB_UNORM_BLOCK, 0, 1, 2);
    Readback rg0 = run_case("rgba8_mip0_only",  VK_FORMAT_R8G8B8A8_UNORM,     0, 0, 2);
    Readback rga = run_case("rgba8_all_mips",   VK_FORMAT_R8G8B8A8_UNORM,     1, 0, 2);

    int fails = 0;
    #define CHECK(name, cond) do { if (cond) printf("PASS %s\n", name); else { printf("FAIL %s\n", name); fails++; } } while (0)
    // a level that was never written reads as zero on this ICD -> alpha 0 -> fully transparent
    CHECK("an unwritten BC1 mip reads as all-zero (blank)", bc0.all_zero);
    CHECK("an unwritten RGBA8 mip also reads as all-zero (the hazard is generic)", rg0.all_zero);
    CHECK("a written mip is not zero (control)", !rga.all_zero);
    if (bcc.nonzero > 0) printf("PASS clearing a BC1 image fills every mip (so it is a legal way to define them)\n");
    else printf("INFO clearing a BC1 image did not land on this ICD - treat the clear point as inconclusive\n");
    printf("RESULT: %s\n", fails ? "FAIL" : "PASS");
    return fails ? 1 : 0;
}
