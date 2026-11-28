// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;

namespace Weims.Core;

/// <summary>
/// The VM's display windows. The runtime's library opens them inside QEMU's process, one per display.
/// The library this project builds (manager/patches/window.patch) takes a registered window message
/// that asks a display's window for full screen; upstream's own ignores it.
/// </summary>
public static class VmWindow
{
    /// <summary>The message's name, as the library registers it (native/windows/remote.rs).</summary>
    public const string FullScreenMessage = "ReimsVgpuWindow.FullScreen";
    /// <summary>What the message asks for, in its wParam.</summary>
    public enum FullScreen { Off = 0, On = 1, Toggle = 2 }

    /// <summary>
    /// The display windows of process <paramref name="pid"/>: its visible top-level windows that no other
    /// window owns, which leaves out the full-screen bar a display's window owns.
    /// </summary>
    public static List<IntPtr> Find(int pid)
    {
        var found = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner == (uint)pid && IsWindowVisible(hwnd) && GetWindow(hwnd, GW_OWNER) == IntPtr.Zero) found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Asks each display window of process <paramref name="pid"/> for <paramref name="what"/> and brings
    /// the first to the front, so the keyboard is the VM's. The number of windows asked.
    /// </summary>
    public static int Ask(int pid, FullScreen what)
    {
        var windows = Find(pid);
        if (windows.Count == 0) return 0;
        uint message = RegisterWindowMessageW(FullScreenMessage);
        if (message == 0) throw new IOException(Text.Get("Windows 拒绝了全屏消息的注册。", "Windows refused to register the full-screen message."));
        foreach (var window in windows) PostMessageW(window, message, (IntPtr)(int)what, IntPtr.Zero);
        SetForegroundWindow(windows[0]);
        return windows.Count;
    }

    const uint GW_OWNER = 4;
    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetForegroundWindow(IntPtr hwnd);
}
