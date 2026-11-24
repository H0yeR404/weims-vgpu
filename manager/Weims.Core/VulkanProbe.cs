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
    [DllImport("vulkan-1.dll")] static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr properties);
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
                // The runtime needs Vulkan 1.3 and checks the features it uses itself as a VM starts, naming what a
                // GPU lacks in the VM's log. VkPhysicalDeviceProperties begins with apiVersion and is under 1 KiB.
                IntPtr properties = Marshal.AllocHGlobal(1024);
                try
                {
                    for (int i = 0; i < count; i++)
                    {
                        vkGetPhysicalDeviceProperties(Marshal.ReadIntPtr(devices, i * IntPtr.Size), properties);
                        uint version = (uint)Marshal.ReadInt32(properties);
                        if (version >> 22 > 1 || (version >> 22 == 1 && ((version >> 12) & 0x3ff) >= 3))
                        { detail = Text.Get("已检测到支持 Vulkan 1.3 的 GPU；最终兼容性以启动为准。", "A GPU with Vulkan 1.3 was found; final compatibility is checked at VM startup."); return true; }
                    }
                }
                finally { Marshal.FreeHGlobal(properties); }
                detail = Text.Get("没有支持 Vulkan 1.3 的 GPU；请更新显卡驱动。", "No GPU supports Vulkan 1.3; update your GPU driver."); return false;
            }
            finally { Marshal.FreeHGlobal(devices); }
        }
        finally { vkDestroyInstance(instance, IntPtr.Zero); }
    }
}
