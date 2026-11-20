/*
 * vulkanshim_stub - the CI stand-in for the port's libvulkanshim.so.
 *
 * The Android build ships a native shim that carries a handful of diagnostics helpers (frame
 * maintenance, cache directory, compatibility summary, present counter, device-lost probe, idle
 * command-pool trim) next to the Vulkan loader. Only RwHost.dll imports them, and RwHost cannot even
 * be loaded on linux-x64 (it is a native-mixed arm64 assembly), but a missing `vulkanshim` still
 * aborts the first `Brutal.VulkanApi.Vulkan` type initialiser with DllNotFoundException before any
 * loader call is made. Same trick as natives/steam_stub.c: provide the exports, return neutral
 * values, and let the real work go through libvulkan.
 *
 * Nothing here is part of the mod or of the measurement; it only lets a bare x64 process reach the
 * real loader.
 */
#include <stddef.h>

#if defined(_WIN32)
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

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
