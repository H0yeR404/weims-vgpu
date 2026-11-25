// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Weims.Broker;

/// <summary>
/// The broker's only window: invisible and message-only. Its thread receives clipboard change
/// notifications and foreground-window events, and runs everything that touches the clipboard.
/// The foreground hook is out-of-process: nothing is injected into QEMU or any other program.
/// </summary>
public sealed class HostWindow(int qemuPid, string vmKey, Action<string> log) : IHostClipboard
{
    const int WM_TIMER = 0x0113, WM_CLIPBOARDUPDATE = 0x031D, WM_APP = 0x8000, CF_UNICODETEXT = 13, CF_HDROP = 15;
    /// <summary>More files than this in one copy are not looked at: the list would not fit a frame anyway.</summary>
    const int FileLimit = 100_000;
    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0, GMEM_MOVEABLE = 2;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX { public int cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm; }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG message);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint milliseconds, IntPtr callback);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(IntPtr drop, uint index, [Out] char[]? name, uint size);

    readonly ConcurrentQueue<Action> posted = new();
    readonly byte[] origin = Encoding.ASCII.GetBytes(vmKey);
    WndProc? procedure; WinEventProc? foregroundProcedure;
    IntPtr window;
    uint originFormat, excludeFormat, historyFormat, cloudFormat, effectFormat;
    bool foreground;

    /// <summary>The Windows clipboard changed. The argument is true when this broker wrote it from guest content.</summary>
    public event Action<bool>? ClipboardChanged;
    /// <summary>The VM window came to the foreground (true) or left it (false).</summary>
    public event Action<bool>? FocusChanged;

    /// <summary>Creates the window on the calling thread and pumps messages until <see cref="Quit"/>.</summary>
    public void Run(Action ready)
    {
        procedure = Handle;
        var windowClass = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure), hInstance = GetModuleHandleW(null), lpszClassName = "WeimsBrokerWindow" };
        if (RegisterClassExW(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        window = CreateWindowExW(0, windowClass.lpszClassName, "Weims Broker", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, windowClass.hInstance, IntPtr.Zero);
        if (window == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        originFormat = RegisterClipboardFormatW("WeimsBrokerOrigin");
        // The three formats Windows documents for content that must stay out of clipboard history and cloud sync.
        excludeFormat = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        historyFormat = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        cloudFormat = RegisterClipboardFormatW("CanUploadToCloudClipboard");
        // What Explorer reads to tell a copy from a cut.
        effectFormat = RegisterClipboardFormatW("Preferred DropEffect");
        if (!AddClipboardFormatListener(window)) throw new Win32Exception(Marshal.GetLastWin32Error());
        foregroundProcedure = (_, _, _, _, _, _, _) => Guard(CheckForeground);
        var hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundProcedure, 0, 0, WINEVENT_OUTOFCONTEXT);
        // The foreground event is not delivered in every case (for example when a minimised window is restored), so also look once a second.
        SetTimer(window, 1, 1000, IntPtr.Zero);
        foreground = IsForeground();
        try
        {
            ready();
            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0) DispatchMessageW(ref message);
        }
        finally
        {
            if (hook != IntPtr.Zero) UnhookWinEvent(hook);
            RemoveClipboardFormatListener(window);
            DestroyWindow(window);
        }
    }

    /// <summary>Runs an action on the window thread.</summary>
    public void Post(Action action) { posted.Enqueue(action); PostMessageW(window, WM_APP, IntPtr.Zero, IntPtr.Zero); }
    public void Quit() => Post(() => PostQuitMessage(0));
    public bool Foreground => foreground;

    IntPtr Handle(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_CLIPBOARDUPDATE: Guard(() => ClipboardChanged?.Invoke(WrittenByThisVm())); return IntPtr.Zero;
            case WM_TIMER: Guard(CheckForeground); return IntPtr.Zero;
            case WM_APP: while (posted.TryDequeue(out var action)) Guard(action); return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }
    // An exception must not unwind into the Windows message dispatcher.
    void Guard(Action action) { try { action(); } catch (Exception e) { log("window thread: " + e); } }

    bool IsForeground()
    {
        var front = GetForegroundWindow();
        if (front == IntPtr.Zero) return false;
        GetWindowThreadProcessId(front, out var process);
        return process == (uint)qemuPid;
    }
    void CheckForeground()
    {
        bool now = IsForeground();
        if (now == foreground) return;
        foreground = now;
        FocusChanged?.Invoke(now);
    }

    // Another program may hold the clipboard for a moment; try a few times, briefly.
    bool Open()
    {
        for (int i = 0; i < 10; i++) { if (OpenClipboard(window)) return true; Thread.Sleep(20); }
        return false;
    }

    /// <summary>True when the clipboard holds text or files that this broker did not put there for this VM.</summary>
    public bool HasForeignContent()
    {
        if (!Open()) return false;
        try { return (IsClipboardFormatAvailable(CF_UNICODETEXT) || IsClipboardFormatAvailable(CF_HDROP)) && !OriginIsThisVm(); }
        finally { CloseClipboard(); }
    }
    bool WrittenByThisVm()
    {
        if (!Open()) return false;
        try { return OriginIsThisVm(); }
        finally { CloseClipboard(); }
    }
    bool OriginIsThisVm() => Bytes(originFormat, 64) is { } marker && marker.AsSpan().SequenceEqual(origin);

    /// <summary>Copies up to <paramref name="max"/> bytes of a clipboard format. The clipboard must be open.</summary>
    static byte[]? Bytes(uint format, int max)
    {
        if (!IsClipboardFormatAvailable(format)) return null;
        var memory = GetClipboardData(format);
        if (memory == IntPtr.Zero) return null;
        var size = GlobalSize(memory).ToUInt64();
        if (size > (ulong)max) return null;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) return null;
        try { var data = new byte[(int)size]; Marshal.Copy(pointer, data, 0, data.Length); return data; }
        finally { GlobalUnlock(memory); }
    }
    bool Sensitive()
    {
        if (IsClipboardFormatAvailable(excludeFormat)) return true;
        bool Zero(uint format) => Bytes(format, 16) is { Length: >= 4 } value && BitConverter.ToUInt32(value) == 0;
        return Zero(historyFormat) || Zero(cloudFormat);
    }

    public ClipContent? Read()
    {
        if (!Open()) return null;
        try
        {
            // Files first: Explorer puts no text beside them, and a program that does means the files.
            if (Files() is { Count: > 0 } files) return new ClipContent("", false) { Files = files };
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            var memory = GetClipboardData(CF_UNICODETEXT);
            if (memory == IntPtr.Zero) return null;
            // UTF-16 never needs fewer bytes than half its UTF-8 form, so anything past this cannot fit the limit.
            if (GlobalSize(memory).ToUInt64() > (ulong)ClipboardSync.TextLimit * 2 + 2) return new ClipContent("", false, true);
            var pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                var text = Marshal.PtrToStringUni(pointer, (int)(GlobalSize(memory).ToUInt64() / 2));
                int end = text.IndexOf('\0');
                return new ClipContent(end < 0 ? text : text[..end], Sensitive());
            }
            finally { GlobalUnlock(memory); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>The paths of the copied files and folders, cut as well as copied. The clipboard must be open.</summary>
    static List<string>? Files()
    {
        if (!IsClipboardFormatAvailable(CF_HDROP)) return null;
        var drop = GetClipboardData(CF_HDROP);
        if (drop == IntPtr.Zero) return null;
        uint count = Math.Min(DragQueryFileW(drop, uint.MaxValue, null, 0), FileLimit);
        var paths = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            uint length = DragQueryFileW(drop, i, null, 0);
            if (length == 0) continue;
            var name = new char[length + 1];
            paths.Add(new string(name, 0, (int)DragQueryFileW(drop, i, name, (uint)name.Length)));
        }
        return paths;
    }

    /// <summary>The list of files as Explorer takes it: a DROPFILES header, then each path and a last empty one, in UTF-16.</summary>
    public static byte[] DropList(IReadOnlyList<string> paths)
    {
        var names = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => p + "\0")) + "\0");
        var list = new byte[20 + names.Length];
        list[0] = 20;       // pFiles: where the names begin
        list[16] = 1;       // fWide
        names.CopyTo(list, 20);
        return list;
    }

    public bool Write(ClipContent content)
    {
        if (content.Files.Count > 0) return WriteFiles(content.Files);
        int end = content.Text.IndexOf('\0');
        var text = end < 0 ? content.Text : content.Text[..end];
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            if (!Set(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"))) return false;
            // The origin mark is how the next change notification, and a restarted broker, know this text came from the guest.
            Set(originFormat, origin);
            if (content.Sensitive) { Set(excludeFormat, new byte[4]); Set(historyFormat, new byte[4]); Set(cloudFormat, new byte[4]); }
            return true;
        }
        finally { CloseClipboard(); }
    }
    bool WriteFiles(IReadOnlyList<string> paths)
    {
        if (!Open()) return false;
        try
        {
            EmptyClipboard();
            if (!Set(CF_HDROP, DropList(paths))) return false;
            Set(originFormat, origin);
            // A copy: pasting leaves the fetched files where they are, for the next paste.
            Set(effectFormat, BitConverter.GetBytes(1));
            return true;
        }
        finally { CloseClipboard(); }
    }
    static bool Set(uint format, byte[] data)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (memory == IntPtr.Zero) return false;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) { GlobalFree(memory); return false; }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        // On success the clipboard owns the memory.
        if (SetClipboardData(format, memory) != IntPtr.Zero) return true;
        GlobalFree(memory);
        return false;
    }
}
