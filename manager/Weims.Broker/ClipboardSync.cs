// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json.Nodes;

namespace Weims.Broker;

/// <param name="TooLarge">The clipboard holds more text than is ever synchronised; <see cref="Text"/> is empty.</param>
public sealed record ClipContent(string Text, bool Sensitive, bool TooLarge = false);

public interface IHostClipboard
{
    /// <summary>The text on the Windows clipboard, or null when it holds none or cannot be opened.</summary>
    ClipContent? Read();
    /// <summary>Puts text on the Windows clipboard, marked as having come from this VM.</summary>
    bool Write(ClipContent content);
}

/// <summary>
/// Decides when clipboard text crosses between Windows and the guest. It touches no Windows API and no
/// socket, and every method runs on one thread (the broker's window thread), so it needs no locks.
///
/// The rules follow the VM window's focus:
/// - In the foreground, changes on either side cross at once.
/// - Coming to the foreground, the Windows clipboard is sent only if it changed while the window was
///   away and that content did not come from this VM. Sending it unconditionally would replace
///   something copied in macOS (say an image) with stale Windows text on every switch.
/// - Leaving the foreground, the guest is asked once for its latest content, so that "copy in macOS,
///   switch to Windows straight away" does not lose it.
/// - In the background, nothing crosses. Several VMs can run; only the one in front sees the clipboard.
/// </summary>
public sealed class ClipboardSync(IHostClipboard host, Func<string> mode, Action<string?> note, Func<DateTime>? clock = null)
{
    public const int TextLimit = 4 * 1024 * 1024;
    public static readonly TimeSpan BlurReplyWindow = TimeSpan.FromSeconds(3);
    static readonly UTF8Encoding Strict = new(false, true);
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    IHelperLink? helper;
    bool focused, hostDirty;
    long sequence, blurRequest;
    DateTime blurDeadline;

    /// <param name="hostHasForeignText">The Windows clipboard holds text that did not come from this VM.</param>
    public void Start(bool hostHasForeignText, bool windowFocused) { hostDirty = hostHasForeignText; focused = windowFocused; }

    public void HelperConnected(IHelperLink link)
    {
        helper = link;
        SendFocus();
        if (focused) PushHostIfDirty();
    }
    public void HelperDisconnected(IHelperLink link) { if (ReferenceEquals(helper, link)) helper = null; }

    /// <summary>The Windows clipboard changed.</summary>
    /// <param name="fromThisVm">The change is this broker's own write of guest content.</param>
    public void HostChanged(bool fromThisVm)
    {
        hostDirty = !fromThisVm;
        if (focused) PushHostIfDirty();
    }

    public void FocusChanged(bool nowFocused)
    {
        if (nowFocused == focused) return;
        focused = nowFocused;
        SendFocus();
        if (focused) { PushHostIfDirty(); return; }
        if (helper == null || mode() == "off") return;
        blurDeadline = now() + BlurReplyWindow;
        _ = helper.Send(new JsonObject { ["type"] = "clipboard_request", ["id"] = ++blurRequest });
    }

    /// <summary>A frame from the connected helper.</summary>
    public void FromGuest(IHelperLink from, Frame frame)
    {
        if (!ReferenceEquals(from, helper) || frame.Type != "clipboard") return;
        bool reply = !focused && blurRequest != 0 && frame.Number("reply_to") == blurRequest && now() <= blurDeadline;
        if (!focused && !reply) return;
        if (reply) blurDeadline = default;
        if (mode() == "off" || frame.Text("kind") != "text") return;
        if (frame.Body.Length > TextLimit) { note("guest clipboard text exceeds the 4 MiB limit and was not copied"); return; }
        string text;
        try { text = Strict.GetString(frame.Body.Span); }
        catch (DecoderFallbackException) { return; }
        if (text.Length == 0) return;
        note(null);
        if (host.Write(new ClipContent(ToWindows(text), frame.Flag("sensitive")))) hostDirty = false;
    }

    void SendFocus() => _ = helper?.Send(new JsonObject { ["type"] = "focus", ["active"] = focused });

    void PushHostIfDirty()
    {
        // Stays dirty until there is a helper to give it to.
        if (!hostDirty || helper == null || mode() == "off") return;
        hostDirty = false;
        var content = host.Read();
        if (content == null) return;
        if (content.TooLarge) { note("Windows clipboard text exceeds the 4 MiB limit and was not copied"); return; }
        var bytes = Encoding.UTF8.GetBytes(ToGuest(content.Text));
        if (bytes.Length == 0) return;
        if (bytes.Length > TextLimit) { note("Windows clipboard text exceeds the 4 MiB limit and was not copied"); return; }
        note(null);
        var header = new JsonObject { ["type"] = "clipboard", ["kind"] = "text", ["seq"] = ++sequence };
        if (content.Sensitive) header["sensitive"] = true;
        _ = helper.Send(header, bytes);
    }

    // Windows programs expect CR LF in clipboard text and macOS programs expect LF, as RDP and VMware also assume.
    public static string ToGuest(string text) => text.Replace("\r\n", "\n");
    public static string ToWindows(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
