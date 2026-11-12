// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;

namespace Weims.Core;

internal static class VulkanProbe
{
    [StructLayout(LayoutKind.Sequential)] struct InstanceInfo
    {
        public uint Type; public IntPtr Next; public uint Flags; public IntPtr App;
        public uint LayerCount; public IntPtr Layers; public uint ExtensionCount; public IntPtr Extensions;
    }
    [DllImport("vulkan-1.dll")] static extern int vkCreateInstance(ref InstanceInfo info, IntPtr allocator, out IntPtr instance);
    [DllImport("vulkan-1.dll")] static extern void vkDestroyInstance(IntPtr instance, IntPtr allocator);
    [DllImport("vulkan-1.dll")] static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);
    [DllImport("vulkan-1.dll")] static extern int vkEnumerateDeviceExtensionProperties(IntPtr device, IntPtr layer, ref uint count, IntPtr properties);
    public static bool Check(out string detail)
    {
        var info = new InstanceInfo { Type = 1 };
        int result = vkCreateInstance(ref info, IntPtr.Zero, out var instance);
        if (result != 0) { detail = "vkCreateInstance: " + result; return false; }
        try
        {
            uint count = 0; if (vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) != 0 || count == 0 || count > 64) { detail = Text.Get("没有可用 GPU；请安装显卡厂商驱动。", "No usable GPU; install the GPU vendor driver."); return false; }
            IntPtr devices = Marshal.AllocHGlobal(checked((int)count * IntPtr.Size));
            try
            {
                if (vkEnumeratePhysicalDevices(instance, ref count, devices) != 0) throw new IOException("Cannot enumerate Vulkan devices.");
                for (int i = 0; i < count; i++)
                {
                    IntPtr device = Marshal.ReadIntPtr(devices, i * IntPtr.Size); uint n = 0;
                    if (vkEnumerateDeviceExtensionProperties(device, IntPtr.Zero, ref n, IntPtr.Zero) != 0 || n > 4096) continue;
                    IntPtr extensions = Marshal.AllocHGlobal(checked((int)n * 260));
                    try
                    {
                        if (vkEnumerateDeviceExtensionProperties(device, IntPtr.Zero, ref n, extensions) != 0) continue;
                        for (int j = 0; j < n; j++) if (Marshal.PtrToStringAnsi(extensions + j * 260) == "VK_EXT_external_memory_host")
                        { detail = Text.Get("已检测到 GPU 与共享内存扩展；最终兼容性以启动为准。", "GPU and shared-memory extension found; final compatibility is checked at VM startup."); return true; }
                    }
                    finally { Marshal.FreeHGlobal(extensions); }
                }
                detail = Text.Get("GPU 缺少 VK_EXT_external_memory_host；请更新显卡驱动。", "GPU lacks VK_EXT_external_memory_host; update your GPU driver."); return false;
            }
            finally { Marshal.FreeHGlobal(devices); }
        }
        finally { vkDestroyInstance(instance, IntPtr.Zero); }
    }
}
