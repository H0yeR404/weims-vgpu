// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using System.Text.Json.Nodes;
using Weims.Core;

namespace Weims.Broker;

/// <param name="TooLarge">The clipboard holds more text than is ever synchronised; <see cref="Text"/> is empty.</param>
public sealed record ClipContent(string Text, bool Sensitive, bool TooLarge = false)
{
    /// <summary>The full paths of copied files and folders. When there are any they are the content, and <see cref="Text"/> is not used.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

public interface IHostClipboard
{
    /// <summary>The files or the text on the Windows clipboard, or null when it holds neither or cannot be opened.</summary>
    ClipContent? Read();
    /// <summary>Puts text or files on the Windows clipboard, marked as having come from this VM.</summary>
    bool Write(ClipContent content);
}

/// <summary>
/// Decides when clipboard content crosses between Windows and the guest. It touches no Windows API and no
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
///
/// Files follow the same rules with two differences (see <see cref="FileTransfer"/>). Files copied in
/// Windows are not sent: the guest is told where it can read them. Files copied in macOS are fetched only
/// when the window is left, never while it is in front, because fetching takes time and room; they are put
/// on the Windows clipboard once they are all here, unless something else was copied in Windows meanwhile.
/// </summary>
public sealed class ClipboardSync(IHostClipboard host, Func<string> mode, Action<string?> note, Func<DateTime>? clock = null, IFileExchange? files = null, Func<bool>? filesOn = null)
{
    public const int TextLimit = 4 * 1024 * 1024;
    public static readonly TimeSpan BlurReplyWindow = TimeSpan.FromSeconds(3);
    static readonly UTF8Encoding Strict = new(false, true);
    readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);
    IHelperLink? helper;
    bool focused, hostDirty;
    long sequence, blurRequest, hostChanges, fetchingSince;
    DateTime blurDeadline;
    /// <summary>The place in the inbox that the helper is filling, while it is.</summary>
    string? fetching;

    /// <param name="hostHasForeignContent">The Windows clipboard holds text or files that did not come from this VM.</param>
    public void Start(bool hostHasForeignContent, bool windowFocused) { hostDirty = hostHasForeignContent; focused = windowFocused; }

    public void HelperConnected(IHelperLink link)
    {
        helper = link;
        SendFocus();
        if (focused) PushHostIfDirty();
    }
    public void HelperDisconnected(IHelperLink link)
    {
        if (!ReferenceEquals(helper, link)) return;
        // Nobody is left to finish it.
        if (fetching != null) { files!.Discard(fetching); fetching = null; note(null); }
        helper = null;
    }

    /// <summary>The Windows clipboard changed.</summary>
    /// <param name="fromThisVm">The change is this broker's own write of guest content.</param>
    public void HostChanged(bool fromThisVm)
    {
        hostChanges++;
        hostDirty = !fromThisVm;
        // What is being fetched from the guest would be put over something newer.
        if (!fromThisVm) StopFetching();
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
        if (!ReferenceEquals(from, helper)) return;
        switch (frame.Type)
        {
            case "clipboard": Clipboard(frame); break;
            case "files_progress": Progress(frame); break;
            case "files_done": Arrived(frame); break;
        }
    }

    void Clipboard(Frame frame)
    {
        bool reply = !focused && blurRequest != 0 && frame.Number("reply_to") == blurRequest && now() <= blurDeadline;
        if (!focused && !reply) return;
        if (reply) blurDeadline = default;
        if (mode() == "off") return;
        string? kind = frame.Text("kind");
        if (kind == "files")
        {
            // Only as the answer to the question asked on the way out; see the class comment.
            if (!reply || !FilesCross) return;
            StopFetching();
            if (files!.Receive() is not var (id, limit)) { note(Text.Get("无法为 macOS 里复制的文件准备位置，详见 broker 日志。", "No place could be made for the files copied in macOS; see the broker's log.")); return; }
            fetching = id; fetchingSince = hostChanges;
            note(Text.Get("正在取回 macOS 里复制的文件…", "Fetching the files copied in macOS…"));
            _ = helper!.Send(new JsonObject { ["type"] = "files_fetch", ["id"] = id, ["share"] = ShareConfig.TransferName, ["limit"] = limit });
            return;
        }
        if (kind != "text") return;
        // Text on the guest's pasteboard means the files are no longer on it.
        StopFetching();
        if (frame.Body.Length > TextLimit) { note(TooMuchText(false)); return; }
        string text;
        try { text = Strict.GetString(frame.Body.Span); }
        catch (DecoderFallbackException) { return; }
        if (text.Length == 0) return;
        note(null);
        if (host.Write(new ClipContent(ToWindows(text), frame.Flag("sensitive")))) hostDirty = false;
    }

    bool FilesCross => files != null && mode() != "off" && filesOn?.Invoke() != false && helper?.Capabilities.Contains(FileTransfer.Capability) == true;

    void StopFetching()
    {
        if (fetching == null) return;
        _ = helper?.Send(new JsonObject { ["type"] = "files_cancel", ["id"] = fetching });
        files!.Discard(fetching); fetching = null;
        note(null);
    }

    void Progress(Frame frame)
    {
        if (fetching == null || frame.Text("id") != fetching || frame.Number("done") is not { } done || frame.Number("total") is not { } total || done < 0 || total < done) return;
        note(Text.Get($"正在取回 macOS 里复制的文件：{done >> 20} / {total >> 20} MiB", $"Fetching the files copied in macOS: {done >> 20} of {total >> 20} MiB"));
    }

    void Arrived(Frame frame)
    {
        if (fetching == null || frame.Text("id") != fetching) return;
        var id = fetching; fetching = null;
        if (!frame.Flag("ok"))
        {
            files!.Discard(id);
            var reason = new string((frame.Text("reason") ?? "").Where(c => !char.IsControl(c)).Take(200).ToArray());
            note(reason switch
            {
                "cancelled" => null,
                "too-large" => Text.Get($"macOS 里复制的文件超过 {FileTransfer.InboundLimit >> 20} MiB，没有取回。大文件请用共享文件夹。", $"The files copied in macOS are more than {FileTransfer.InboundLimit >> 20} MiB and were not fetched. Use a shared folder for large files."),
                _ => Text.Get("macOS 里复制的文件没有取回：", "The files copied in macOS were not fetched: ") + reason
            });
            return;
        }
        // Something was copied in Windows after the fetch began and before this could stop it.
        if (fetchingSince != hostChanges) { files!.Discard(id); note(null); return; }
        var paths = files!.Received(id);
        if (paths.Count == 0) { note(Text.Get("macOS 里复制的文件没有取回：没有收到任何文件。", "The files copied in macOS were not fetched: nothing arrived.")); return; }
        note(null);
        if (host.Write(new ClipContent("", false) { Files = paths })) hostDirty = false;
    }

    static string TooMuchText(bool windows) => windows
        ? Text.Get("Windows 剪贴板里的文本超过 4 MiB，没有复制到 macOS。", "The text on the Windows clipboard is over 4 MiB and was not copied to macOS.")
        : Text.Get("macOS 剪贴板里的文本超过 4 MiB，没有复制到 Windows。", "The text on the macOS clipboard is over 4 MiB and was not copied to Windows.");

    void SendFocus() => _ = helper?.Send(new JsonObject { ["type"] = "focus", ["active"] = focused });

    void PushHostIfDirty()
    {
        // Stays dirty until there is a helper to give it to.
        if (!hostDirty || helper == null || mode() == "off") return;
        hostDirty = false;
        var content = host.Read();
        if (content == null) return;
        if (content.Files.Count > 0) { PushFiles(content.Files); return; }
        if (content.TooLarge) { note(TooMuchText(true)); return; }
        var bytes = Encoding.UTF8.GetBytes(ToGuest(content.Text));
        if (bytes.Length == 0) return;
        if (bytes.Length > TextLimit) { note(TooMuchText(true)); return; }
        note(null);
        var header = new JsonObject { ["type"] = "clipboard", ["kind"] = "text", ["seq"] = ++sequence };
        if (content.Sensitive) header["sensitive"] = true;
        _ = helper!.Send(header, bytes);
    }

    void PushFiles(IReadOnlyList<string> paths)
    {
        if (!FilesCross) return;
        if (files!.Offer(paths) is not var (items, leftOut))
        {
            note(Text.Get("Windows 里复制的文件无法提供给 macOS：只支持本机磁盘上的文件和文件夹。", "The files copied in Windows cannot be offered to macOS: only files and folders on this PC's drives can."));
            return;
        }
        note(leftOut == 0 ? null : Text.Get($"Windows 里复制的项目有 {leftOut} 个无法提供给 macOS（网络位置、整个磁盘，或来自太多不同的文件夹）。", $"{leftOut} of the items copied in Windows cannot be offered to macOS (network places, whole drives, or too many different folders)."));
        // The places are the body, one to a line: there can be thousands, and no Windows name holds a line break.
        _ = helper!.Send(new JsonObject { ["type"] = "clipboard", ["kind"] = "files", ["seq"] = ++sequence, ["share"] = ShareConfig.TransferName }, Encoding.UTF8.GetBytes(string.Join('\n', items)));
    }

    // Windows programs expect CR LF in clipboard text and macOS programs expect LF, as RDP and VMware also assume.
    public static string ToGuest(string text) => text.Replace("\r\n", "\n");
    public static string ToWindows(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
