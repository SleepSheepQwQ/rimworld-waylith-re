// Offscreen renderer for the CutoutComplexUI material path, so the icon-tile behaviour can be
// measured on CI (software Vulkan / lavapipe) instead of on a phone.
//
// It runs the real fragment shader with:
//   * the real icon texture (raw RGBA8 extracted from the APK's Assets.zip),
//   * the real mask texture,
//   * the material uniforms the engine builds (MaterialUniforms: _Color, _MainTex_ST, _ColorTwo),
//   * a full-quad uv range, i.e. exactly what ImGuiMaterialRenderer hands the shader.
// Then it counts how many of the rendered pixels survive the shader's `if (f.a < 0.5) discard;`.
//
// usage: harness <frag.spv> <vert.spv> <main.rgba> <w> <h> <mask.rgba|-> <stx> <sty> <stz> <stw> <out.ppm>

#include <vulkan/vulkan.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

static void die(const char *msg) { fprintf(stderr, "harness: %s\n", msg); exit(2); }
static void chk(VkResult r, const char *what) {
    if (r != VK_SUCCESS) { fprintf(stderr, "harness: %s -> VkResult %d\n", what, (int)r); exit(2); }
}

static unsigned char *read_all(const char *path, size_t *size)
{
    FILE *f = fopen(path, "rb");
    if (!f) { fprintf(stderr, "harness: cannot open %s\n", path); exit(2); }
    fseek(f, 0, SEEK_END); long n = ftell(f); fseek(f, 0, SEEK_SET);
    unsigned char *b = malloc((size_t)n);
    if (fread(b, 1, (size_t)n, f) != (size_t)n) die("short read");
    fclose(f);
    *size = (size_t)n;
    return b;
}

/* ---------------------------------------------------------------- images */

typedef struct { VkImage img; VkDeviceMemory mem; VkImageView view; uint32_t w, h; } Image;

static uint32_t find_mem_type(VkPhysicalDevice pd, uint32_t bits, VkMemoryPropertyFlags want)
{
    VkPhysicalDeviceMemoryProperties mp;
    vkGetPhysicalDeviceMemoryProperties(pd, &mp);
    for (uint32_t i = 0; i < mp.memoryTypeCount; i++)
        if ((bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) return i;
    die("no suitable memory type");
    return 0;
}

static Image make_image(VkPhysicalDevice pd, VkDevice dev, VkFormat fmt, uint32_t w, uint32_t h,
                        VkImageUsageFlags usage, VkImageLayout initial)
{
    Image im = {0}; im.w = w; im.h = h;
    VkImageCreateInfo ci = {0};
    ci.sType = VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO;
    ci.imageType = VK_IMAGE_TYPE_2D;
    ci.format = fmt;
    ci.extent.width = w; ci.extent.height = h; ci.extent.depth = 1;
    ci.mipLevels = 1; ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT;
    ci.tiling = VK_IMAGE_TILING_OPTIMAL;
    ci.usage = usage;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    ci.initialLayout = initial;
    chk(vkCreateImage(dev, &ci, NULL, &im.img), "vkCreateImage");

    VkMemoryRequirements req; vkGetImageMemoryRequirements(dev, im.img, &req);
    VkMemoryAllocateInfo ai = {0};
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = find_mem_type(pd, req.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
    chk(vkAllocateMemory(dev, &ai, NULL, &im.mem), "vkAllocateMemory(image)");
    chk(vkBindImageMemory(dev, im.img, im.mem, 0), "vkBindImageMemory");

    VkImageViewCreateInfo vi = {0};
    vi.sType = VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO;
    vi.image = im.img;
    vi.viewType = VK_IMAGE_VIEW_TYPE_2D;
    vi.format = fmt;
    vi.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    vi.subresourceRange.levelCount = 1;
    vi.subresourceRange.layerCount = 1;
    chk(vkCreateImageView(dev, &vi, NULL, &im.view), "vkCreateImageView");
    return im;
}

static void *make_buffer(VkPhysicalDevice pd, VkDevice dev, VkDeviceSize size, VkBufferUsageFlags usage,
                         VkBuffer *out, VkDeviceMemory *outMem, VkMemoryPropertyFlags props)
{
    VkBufferCreateInfo bi = {0};
    bi.sType = VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO;
    bi.size = size; bi.usage = usage; bi.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    chk(vkCreateBuffer(dev, &bi, NULL, out), "vkCreateBuffer");

    VkMemoryRequirements req; vkGetBufferMemoryRequirements(dev, *out, &req);
    VkMemoryAllocateInfo ai = {0};
    ai.sType = VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO;
    ai.allocationSize = req.size;
    ai.memoryTypeIndex = find_mem_type(pd, req.memoryTypeBits, props);
    chk(vkAllocateMemory(dev, &ai, NULL, outMem), "vkAllocateMemory(buffer)");
    chk(vkBindBufferMemory(dev, *out, *outMem, 0), "vkBindBufferMemory");

    void *p = NULL;
    if (props & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT)
        chk(vkMapMemory(dev, *outMem, 0, size, 0, &p), "vkMapMemory");
    return p;
}

static void barrier(VkCommandBuffer cb, VkImage img,
                    VkImageLayout from, VkImageLayout to,
                    VkAccessFlags srcAccess, VkAccessFlags dstAccess,
                    VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage)
{
    VkImageMemoryBarrier b = {0};
    b.sType = VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER;
    b.oldLayout = from; b.newLayout = to;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img;
    b.subresourceRange.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    b.subresourceRange.levelCount = 1;
    b.subresourceRange.layerCount = 1;
    b.srcAccessMask = srcAccess; b.dstAccessMask = dstAccess;
    vkCmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, NULL, 0, NULL, 1, &b);
}

/* ---------------------------------------------------------------- main */

int main(int argc, char **argv)
{
    if (argc != 12) {
        fprintf(stderr, "usage: %s <frag.spv> <vert.spv> <main.rgba> <w> <h> <mask.rgba|-> "
                        "<stx> <sty> <stz> <stw> <out.ppm>\n", argv[0]);
        return 2;
    }
    const char *fragPath = argv[1], *vertPath = argv[2], *mainPath = argv[3];
    uint32_t W = (uint32_t)atoi(argv[4]), H = (uint32_t)atoi(argv[5]);
    const char *maskPath = argv[6];
    float stx = (float)atof(argv[7]), sty = (float)atof(argv[8]);
    float stz = (float)atof(argv[9]), stw = (float)atof(argv[10]);
    const char *ppmPath = argv[11];

    /* instance + device */
    VkApplicationInfo app = {0};
    app.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    app.apiVersion = VK_API_VERSION_1_1;
    VkInstanceCreateInfo ici = {0};
    ici.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    ici.pApplicationInfo = &app;
    VkInstance inst;
    chk(vkCreateInstance(&ici, NULL, &inst), "vkCreateInstance");

    uint32_t nd = 0;
    vkEnumeratePhysicalDevices(inst, &nd, NULL);
    if (nd == 0) die("no Vulkan physical device");
    VkPhysicalDevice *pds = malloc(nd * sizeof(*pds));
    vkEnumeratePhysicalDevices(inst, &nd, pds);
    VkPhysicalDevice pd = pds[0];
    VkPhysicalDeviceProperties props; vkGetPhysicalDeviceProperties(pd, &props);
    printf("harness: device '%s' (%s)\n", props.deviceName,
           props.deviceType == VK_PHYSICAL_DEVICE_TYPE_CPU ? "cpu" : "gpu");

    uint32_t nq = 0; vkGetPhysicalDeviceQueueFamilyProperties(pd, &nq, NULL);
    VkQueueFamilyProperties *qs = malloc(nq * sizeof(*qs));
    vkGetPhysicalDeviceQueueFamilyProperties(pd, &nq, qs);
    uint32_t qi = 0; int found = 0;
    for (uint32_t i = 0; i < nq; i++)
        if (qs[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) { qi = i; found = 1; break; }
    if (!found) die("no graphics queue");

    float prio = 1.0f;
    VkDeviceQueueCreateInfo qci = {0};
    qci.sType = VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO;
    qci.queueFamilyIndex = qi; qci.queueCount = 1; qci.pQueuePriorities = &prio;
    VkDeviceCreateInfo dci = {0};
    dci.sType = VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO;
    dci.queueCreateInfoCount = 1; dci.pQueueCreateInfos = &qci;
    VkDevice dev;
    chk(vkCreateDevice(pd, &dci, NULL, &dev), "vkCreateDevice");
    VkQueue q; vkGetDeviceQueue(dev, qi, 0, &q);

    /* textures */
    size_t mainSize = 0, maskSize = 0;
    unsigned char *mainPix = read_all(mainPath, &mainSize);
    if (mainSize < (size_t)W * H * 4) die("main texture too small");
    unsigned char *maskPix = NULL;
    int useMask = strcmp(maskPath, "-") != 0;
    if (useMask) {
        maskPix = read_all(maskPath, &maskSize);
        if (maskSize < (size_t)W * H * 4) die("mask texture too small");
    }

    Image mainImg = make_image(pd, dev, VK_FORMAT_R8G8B8A8_UNORM, W, H,
                               VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                               VK_IMAGE_LAYOUT_UNDEFINED);
    Image maskImg;
    if (useMask)
        maskImg = make_image(pd, dev, VK_FORMAT_R8G8B8A8_UNORM, W, H,
                             VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                             VK_IMAGE_LAYOUT_UNDEFINED);

    /* a black 1x1 mask is what the engine substitutes when the material has no _MaskTex */
    Image blackImg = {0};
    if (!useMask) {
        blackImg = make_image(pd, dev, VK_FORMAT_R8G8B8A8_UNORM, 1, 1,
                              VK_IMAGE_USAGE_SAMPLED_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT,
                              VK_IMAGE_LAYOUT_UNDEFINED);
    }

    Image rt = make_image(pd, dev, VK_FORMAT_R8G8B8A8_UNORM, W, H,
                          VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT,
                          VK_IMAGE_LAYOUT_UNDEFINED);

    /* upload buffers */
    VkBuffer sBuf; VkDeviceMemory sMem;
    size_t sSize = (size_t)W * H * 4;
    void *sp = make_buffer(pd, dev, sSize, VK_BUFFER_USAGE_TRANSFER_SRC_BIT, &sBuf, &sMem,
                           VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);

    /* uniforms: _Color, _MainTex_ST, _ColorTwo (std140 vec4 x3) */
    float ubo[12] = { 1,1,1,1,  stx,sty,stz,stw,  1,1,1,1 };
    VkBuffer uBuf; VkDeviceMemory uMem;
    void *up = make_buffer(pd, dev, sizeof(ubo), VK_BUFFER_USAGE_UNIFORM_BUFFER_BIT, &uBuf, &uMem,
                           VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    memcpy(up, ubo, sizeof(ubo));

    /* vertices: 2 triangles, uv 0..1, colour = the stuff colour seen in the v29 log */
    float vcol[4] = { 0.522f, 0.380f, 0.263f, 1.0f };
    float verts[6][8];
    const float px[4] = { -1, -1, 1, 1 }, py[4] = { -1, 1, -1, 1 };
    const float uu[4] = { 0, 0, 1, 1 }, vv[4] = { 0, 1, 0, 1 };
    const int order[6] = { 0, 1, 2, 1, 3, 2 };
    for (int i = 0; i < 6; i++) {
        int k = order[i];
        verts[i][0] = px[k]; verts[i][1] = py[k];
        verts[i][2] = uu[k]; verts[i][3] = vv[k];
        verts[i][4] = vcol[0]; verts[i][5] = vcol[1]; verts[i][6] = vcol[2]; verts[i][7] = vcol[3];
    }
    VkBuffer vBuf; VkDeviceMemory vMem;
    void *vp = make_buffer(pd, dev, sizeof(verts), VK_BUFFER_USAGE_VERTEX_BUFFER_BIT, &vBuf, &vMem,
                           VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
    memcpy(vp, verts, sizeof(verts));

    /* readback buffer */
    VkBuffer rBuf; VkDeviceMemory rMem;
    void *rbPtr = make_buffer(pd, dev, sSize, VK_BUFFER_USAGE_TRANSFER_DST_BIT, &rBuf, &rMem,
                           VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);

    /* command pool/buffer */
    VkCommandPoolCreateInfo pci = {0};
    pci.sType = VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO;
    pci.queueFamilyIndex = qi;
    VkCommandPool pool;
    chk(vkCreateCommandPool(dev, &pci, NULL, &pool), "vkCreateCommandPool");
    VkCommandBufferAllocateInfo cbi = {0};
    cbi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO;
    cbi.commandPool = pool; cbi.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; cbi.commandBufferCount = 1;
    VkCommandBuffer cb;
    chk(vkAllocateCommandBuffers(dev, &cbi, &cb), "vkAllocateCommandBuffers");

    /* upload the textures */
    VkCommandBufferBeginInfo bi = {0};
    bi.sType = VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO;
    chk(vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer");

    memcpy(sp, mainPix, sSize);
    barrier(cb, mainImg.img, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
            VK_PIPELINE_STAGE_TRANSFER_BIT);
    VkBufferImageCopy bic = {0};
    bic.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    bic.imageSubresource.layerCount = 1;
    bic.imageExtent.width = W; bic.imageExtent.height = H; bic.imageExtent.depth = 1;
    vkCmdCopyBufferToImage(cb, sBuf, mainImg.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &bic);
    barrier(cb, mainImg.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
            VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT,
            VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
            VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);

    if (useMask) {
        memcpy(sp, maskPix, sSize);
        barrier(cb, maskImg.img, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                VK_PIPELINE_STAGE_TRANSFER_BIT);
        vkCmdCopyBufferToImage(cb, sBuf, maskImg.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &bic);
        barrier(cb, maskImg.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT,
                VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
    } else {
        unsigned char black[4] = { 0, 0, 0, 255 };
        memcpy(sp, black, 4);
        barrier(cb, blackImg.img, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                0, VK_ACCESS_TRANSFER_WRITE_BIT, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT,
                VK_PIPELINE_STAGE_TRANSFER_BIT);
        VkBufferImageCopy b1 = bic;
        b1.imageExtent.width = 1; b1.imageExtent.height = 1;
        vkCmdCopyBufferToImage(cb, sBuf, blackImg.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &b1);
        barrier(cb, blackImg.img, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL,
                VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL, VK_ACCESS_TRANSFER_WRITE_BIT,
                VK_ACCESS_SHADER_READ_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT,
                VK_PIPELINE_STAGE_FRAGMENT_SHADER_BIT);
    }
    chk(vkEndCommandBuffer(cb), "vkEndCommandBuffer");
    VkSubmitInfo si = {0};
    si.sType = VK_STRUCTURE_TYPE_SUBMIT_INFO;
    si.commandBufferCount = 1; si.pCommandBuffers = &cb;
    chk(vkQueueSubmit(q, 1, &si, VK_NULL_HANDLE), "vkQueueSubmit(upload)");
    chk(vkQueueWaitIdle(q), "vkQueueWaitIdle(upload)");

    /* descriptor sets: sets 0 and 1 are empty, set 2 is the material set */
    VkDescriptorSetLayout empty;
    VkDescriptorSetLayoutCreateInfo eli = {0};
    eli.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    eli.bindingCount = 0;
    chk(vkCreateDescriptorSetLayout(dev, &eli, NULL, &empty), "vkCreateDescriptorSetLayout(empty)");

    VkDescriptorSetLayoutBinding binds[3] = {0};
    binds[0].binding = 0; binds[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER;
    binds[0].descriptorCount = 1; binds[0].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    binds[1].binding = 1; binds[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    binds[1].descriptorCount = 1; binds[1].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    binds[2].binding = 2; binds[2].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
    binds[2].descriptorCount = 1; binds[2].stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
    VkDescriptorSetLayoutCreateInfo mli = {0};
    mli.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO;
    mli.bindingCount = 3; mli.pBindings = binds;
    VkDescriptorSetLayout matLayout;
    chk(vkCreateDescriptorSetLayout(dev, &mli, NULL, &matLayout), "vkCreateDescriptorSetLayout(material)");

    VkDescriptorSetLayout setLayouts[3] = { empty, empty, matLayout };
    VkPipelineLayoutCreateInfo pli = {0};
    pli.sType = VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO;
    pli.setLayoutCount = 3; pli.pSetLayouts = setLayouts;
    VkPipelineLayout playout;
    chk(vkCreatePipelineLayout(dev, &pli, NULL, &playout), "vkCreatePipelineLayout");

    VkDescriptorPoolSize ps[2] = {
        { VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER, 1 },
        { VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 2 },
    };
    VkDescriptorPoolCreateInfo dpi = {0};
    dpi.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO;
    dpi.maxSets = 1; dpi.poolSizeCount = 2; dpi.pPoolSizes = ps;
    VkDescriptorPool dpool;
    chk(vkCreateDescriptorPool(dev, &dpi, NULL, &dpool), "vkCreateDescriptorPool");

    VkDescriptorSetAllocateInfo dai = {0};
    dai.sType = VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO;
    dai.descriptorPool = dpool; dai.descriptorSetCount = 1; dai.pSetLayouts = &matLayout;
    VkDescriptorSet dset;
    chk(vkAllocateDescriptorSets(dev, &dai, &dset), "vkAllocateDescriptorSets");

    /* sampler + writes */
    VkSamplerCreateInfo sci = {0};
    sci.sType = VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO;
    sci.magFilter = VK_FILTER_LINEAR; sci.minFilter = VK_FILTER_LINEAR;
    sci.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
    sci.addressModeU = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeV = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
    sci.maxLod = 0.0f;
    VkSampler samp;
    chk(vkCreateSampler(dev, &sci, NULL, &samp), "vkCreateSampler");

    VkDescriptorBufferInfo dbi = {0};
    dbi.buffer = uBuf; dbi.offset = 0; dbi.range = sizeof(ubo);

    VkDescriptorImageInfo diiMain = {0};
    diiMain.sampler = samp; diiMain.imageView = mainImg.view;
    diiMain.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
    VkDescriptorImageInfo diiMask = {0};
    diiMask.sampler = samp;
    diiMask.imageView = useMask ? maskImg.view : blackImg.view;
    diiMask.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;

    VkWriteDescriptorSet wr[3] = {0};
    wr[0].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    wr[0].dstSet = dset; wr[0].dstBinding = 0; wr[0].descriptorCount = 1;
    wr[0].descriptorType = VK_DESCRIPTOR_TYPE_UNIFORM_BUFFER; wr[0].pBufferInfo = &dbi;
    wr[1].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    wr[1].dstSet = dset; wr[1].dstBinding = 1; wr[1].descriptorCount = 1;
    wr[1].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; wr[1].pImageInfo = &diiMain;
    wr[2].sType = VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;
    wr[2].dstSet = dset; wr[2].dstBinding = 2; wr[2].descriptorCount = 1;
    wr[2].descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; wr[2].pImageInfo = &diiMask;
    vkUpdateDescriptorSets(dev, 3, wr, 0, NULL);

    /* render pass */
    VkAttachmentDescription att = {0};
    att.format = VK_FORMAT_R8G8B8A8_UNORM;
    att.samples = VK_SAMPLE_COUNT_1_BIT;
    att.loadOp = VK_ATTACHMENT_LOAD_OP_CLEAR;
    att.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
    att.stencilLoadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE;
    att.stencilStoreOp = VK_ATTACHMENT_STORE_OP_DONT_CARE;
    att.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    att.finalLayout = VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL;
    VkAttachmentReference ar = {0};
    ar.attachment = 0; ar.layout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
    VkSubpassDescription sub = {0};
    sub.pipelineBindPoint = VK_PIPELINE_BIND_POINT_GRAPHICS;
    sub.colorAttachmentCount = 1; sub.pColorAttachments = &ar;
    VkRenderPassCreateInfo rpci = {0};
    rpci.sType = VK_STRUCTURE_TYPE_RENDER_PASS_CREATE_INFO;
    rpci.attachmentCount = 1; rpci.pAttachments = &att;
    rpci.subpassCount = 1; rpci.pSubpasses = &sub;
    VkRenderPass renderPass;
    chk(vkCreateRenderPass(dev, &rpci, NULL, &renderPass), "vkCreateRenderPass");

    VkImageView fbView = rt.view;
    VkFramebufferCreateInfo fci = {0};
    fci.sType = VK_STRUCTURE_TYPE_FRAMEBUFFER_CREATE_INFO;
    fci.renderPass = renderPass; fci.attachmentCount = 1; fci.pAttachments = &fbView;
    fci.width = W; fci.height = H; fci.layers = 1;
    VkFramebuffer fb;
    chk(vkCreateFramebuffer(dev, &fci, NULL, &fb), "vkCreateFramebuffer");

    /* shader modules */
    size_t fragN = 0, vertN = 0;
    unsigned char *fragCode = read_all(fragPath, &fragN);
    unsigned char *vertCode = read_all(vertPath, &vertN);
    VkShaderModuleCreateInfo smi = {0};
    smi.sType = VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO;
    smi.codeSize = fragN; smi.pCode = (const uint32_t *)fragCode;
    VkShaderModule fs;
    chk(vkCreateShaderModule(dev, &smi, NULL, &fs), "vkCreateShaderModule(frag)");
    smi.codeSize = vertN; smi.pCode = (const uint32_t *)vertCode;
    VkShaderModule vs;
    chk(vkCreateShaderModule(dev, &smi, NULL, &vs), "vkCreateShaderModule(vert)");

    VkPipelineShaderStageCreateInfo stages[2] = {0};
    stages[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    stages[0].stage = VK_SHADER_STAGE_VERTEX_BIT; stages[0].module = vs; stages[0].pName = "main";
    stages[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
    stages[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; stages[1].module = fs; stages[1].pName = "main";

    VkVertexInputBindingDescription vib = {0};
    vib.binding = 0; vib.stride = 8 * sizeof(float);
    vib.inputRate = VK_VERTEX_INPUT_RATE_VERTEX;
    VkVertexInputAttributeDescription via[3] = {
        { 0, 0, VK_FORMAT_R32G32_SFLOAT, 0 },
        { 1, 0, VK_FORMAT_R32G32_SFLOAT, 2 * sizeof(float) },
        { 2, 0, VK_FORMAT_R32G32B32A32_SFLOAT, 4 * sizeof(float) },
    };
    VkPipelineVertexInputStateCreateInfo vis = {0};
    vis.sType = VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO;
    vis.vertexBindingDescriptionCount = 1; vis.pVertexBindingDescriptions = &vib;
    vis.vertexAttributeDescriptionCount = 3; vis.pVertexAttributeDescriptions = via;

    VkPipelineInputAssemblyStateCreateInfo ias = {0};
    ias.sType = VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO;
    ias.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;

    VkViewport vport = {0, 0, (float)W, (float)H, 0, 1};
    VkRect2D scissor = { {0, 0}, {W, H} };
    VkPipelineViewportStateCreateInfo vps = {0};
    vps.sType = VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO;
    vps.viewportCount = 1; vps.pViewports = &vport;
    vps.scissorCount = 1; vps.pScissors = &scissor;

    VkPipelineRasterizationStateCreateInfo rs = {0};
    rs.sType = VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO;
    rs.polygonMode = VK_POLYGON_MODE_FILL;
    rs.cullMode = VK_CULL_MODE_NONE;
    rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE;
    rs.lineWidth = 1.0f;

    VkPipelineMultisampleStateCreateInfo ms = {0};
    ms.sType = VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO;
    ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;

    VkPipelineColorBlendAttachmentState cba = {0};
    cba.blendEnable = VK_FALSE;
    cba.colorWriteMask = VK_COLOR_COMPONENT_R_BIT | VK_COLOR_COMPONENT_G_BIT |
                         VK_COLOR_COMPONENT_B_BIT | VK_COLOR_COMPONENT_A_BIT;
    VkPipelineColorBlendStateCreateInfo cbs = {0};
    cbs.sType = VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO;
    cbs.attachmentCount = 1; cbs.pAttachments = &cba;

    VkGraphicsPipelineCreateInfo gpci = {0};
    gpci.sType = VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO;
    gpci.stageCount = 2; gpci.pStages = stages;
    gpci.pVertexInputState = &vis;
    gpci.pInputAssemblyState = &ias;
    gpci.pViewportState = &vps;
    gpci.pRasterizationState = &rs;
    gpci.pMultisampleState = &ms;
    gpci.pColorBlendState = &cbs;
    gpci.layout = playout;
    gpci.renderPass = renderPass;
    gpci.subpass = 0;
    VkPipeline pipe;
    chk(vkCreateGraphicsPipelines(dev, VK_NULL_HANDLE, 1, &gpci, NULL, &pipe), "vkCreateGraphicsPipelines");

    /* draw */
    vkResetCommandBuffer(cb, 0);
    chk(vkBeginCommandBuffer(cb, &bi), "vkBeginCommandBuffer(draw)");
    VkClearValue clear = {0};
    clear.color.float32[0] = 0; clear.color.float32[1] = 0;
    clear.color.float32[2] = 0; clear.color.float32[3] = 0;
    VkRenderPassBeginInfo rpbi = {0};
    rpbi.sType = VK_STRUCTURE_TYPE_RENDER_PASS_BEGIN_INFO;
    rpbi.renderPass = renderPass; rpbi.framebuffer = fb;
    rpbi.renderArea.extent.width = W; rpbi.renderArea.extent.height = H;
    rpbi.clearValueCount = 1; rpbi.pClearValues = &clear;
    vkCmdBeginRenderPass(cb, &rpbi, VK_SUBPASS_CONTENTS_INLINE);
    vkCmdBindPipeline(cb, VK_PIPELINE_BIND_POINT_GRAPHICS, pipe);
    vkCmdBindDescriptorSets(cb, VK_PIPELINE_BIND_POINT_GRAPHICS, playout, 2, 1, &dset, 0, NULL);
    VkDeviceSize off = 0;
    vkCmdBindVertexBuffers(cb, 0, 1, &vBuf, &off);
    vkCmdDraw(cb, 6, 1, 0, 0);
    vkCmdEndRenderPass(cb);

    VkBufferImageCopy copy = {0};
    copy.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT;
    copy.imageSubresource.layerCount = 1;
    copy.imageExtent.width = W; copy.imageExtent.height = H; copy.imageExtent.depth = 1;
    vkCmdCopyImageToBuffer(cb, rt.img, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, rBuf, 1, &copy);
    chk(vkEndCommandBuffer(cb), "vkEndCommandBuffer(draw)");
    chk(vkQueueSubmit(q, 1, &si, VK_NULL_HANDLE), "vkQueueSubmit(draw)");
    chk(vkQueueWaitIdle(q), "vkQueueWaitIdle(draw)");

    /* count surviving pixels + write a PPM for eyeballing */
    unsigned char *out = (unsigned char *)rbPtr;
    size_t total = (size_t)W * H, drawn = 0;
    FILE *ppm = fopen(ppmPath, "wb");
    if (ppm) { fprintf(ppm, "P6\n%u %u\n255\n", W, H); }
    for (size_t i = 0; i < total; i++) {
        unsigned char *p = out + i * 4;
        if (p[3] >= 128) drawn++;
        if (ppm) fwrite(p, 1, 3, ppm);
    }
    if (ppm) fclose(ppm);

    printf("harness: st=(%g,%g,%g,%g) mask=%s drawn=%zu/%zu (%.1f%%)\n",
           stx, sty, stz, stw, useMask ? "yes" : "black1x1",
           drawn, total, 100.0 * (double)drawn / (double)total);
    printf("RESULT drawn=%zu total=%zu\n", drawn, total);
    return 0;
}
