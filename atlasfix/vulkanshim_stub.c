/*
 * vulkanshim_stub - the CI stand-in for the port's libvulkanshim.so.
 *
 * The Android build ships a native shim in front of the Vulkan loader. Two things live in it:
 *
 *   1. diagnostics helpers (frame maintenance, cache directory, compatibility summary, present
 *      counter, device-lost probe, idle command-pool trim) imported by RwHost.dll, and
 *   2. the loader entry points the binding fetches by hand (`vkGetInstanceProcAddr` and friends).
 *
 * A bare linux-x64 process has neither, so `Brutal.VulkanApi.Vulkan`'s type initialiser dies with
 * DllNotFoundException("vulkanshim") and, once the library exists but is empty, with
 * EntryPointNotFoundException("vkGetInstanceProcAddr"). This file supplies both halves and forwards
 * the loader entry points to the real libvulkan, i.e. exactly the same trick as
 * natives/steam_stub.c and natives/waylithaudio_stub.c: give the P/Invoke something to bind to and
 * let the real implementation do the work.
 *
 * Nothing here is part of the mod or of the measurement.
 */
#include <dlfcn.h>
#include <stddef.h>

#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

/* ---------------------------------------------------------------- real loader ----- */

static void *g_lib;

static void *lib(void)
{
    if (!g_lib) g_lib = dlopen("libvulkan.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!g_lib) g_lib = dlopen("libvulkan.so", RTLD_NOW | RTLD_LOCAL);
    return g_lib;
}

static void *sym(const char *name)
{
    void *l = lib();
    return l ? dlsym(l, name) : NULL;
}

typedef void *(*PFN_gipa)(void *, const char *);
typedef int (*PFN_create_instance)(const void *, const void *, void **);
typedef int (*PFN_enum_ext)(const char *, int *, void *);
typedef int (*PFN_enum_layer)(int *, void *);
typedef int (*PFN_enum_version)(int *);

EXPORT void *vkGetInstanceProcAddr(void *instance, const char *name)
{
    static PFN_gipa f;
    if (!f) f = (PFN_gipa)sym("vkGetInstanceProcAddr");
    return f ? f(instance, name) : NULL;
}

EXPORT void *vkGetDeviceProcAddr(void *device, const char *name)
{
    static PFN_gipa f;
    if (!f) f = (PFN_gipa)sym("vkGetDeviceProcAddr");
    return f ? f(device, name) : NULL;
}

EXPORT int vkCreateInstance(const void *createInfo, const void *allocator, void **instance)
{
    static PFN_create_instance f;
    if (!f) f = (PFN_create_instance)sym("vkCreateInstance");
    return f ? f(createInfo, allocator, instance) : (int)-3; /* VK_ERROR_INITIALIZATION_FAILED */
}

EXPORT int vkEnumerateInstanceExtensionProperties(const char *layerName, int *count, void *properties)
{
    static PFN_enum_ext f;
    if (!f) f = (PFN_enum_ext)sym("vkEnumerateInstanceExtensionProperties");
    return f ? f(layerName, count, properties) : (int)-3;
}

EXPORT int vkEnumerateInstanceLayerProperties(int *count, void *properties)
{
    static PFN_enum_layer f;
    if (!f) f = (PFN_enum_layer)sym("vkEnumerateInstanceLayerProperties");
    return f ? f(count, properties) : (int)-3;
}

EXPORT int vkEnumerateInstanceVersion(int *apiVersion)
{
    static PFN_enum_version f;
    if (!f) f = (PFN_enum_version)sym("vkEnumerateInstanceVersion");
    return f ? f(apiVersion) : (int)-3;
}

/* ---------------------------------------------------------------- diagnostics ----- */

EXPORT void vkshimRequestMaintenance(void) {}
EXPORT void vkshimSetCacheDirectory(const char *path) { (void)path; }
EXPORT const void *vkshimCompatibilitySummary(void) { return 0; }
EXPORT unsigned long long vkshimSuccessfulPresents(void) { return 0; }
EXPORT int vkshimDeviceLost(void) { return 0; }
EXPORT int vkshimTrimIdleCommandPool(void *device, unsigned long long pool)
{
    (void)device;
    (void)pool;
    return 0;
}
