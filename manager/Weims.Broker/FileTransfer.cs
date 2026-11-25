// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text;
using SMBLibrary;
using SMBLibrary.Win32;
using Weims.Core;
using SmbAttributes = SMBLibrary.FileAttributes;

namespace Weims.Broker;

/// <summary>What the clipboard rules need of the files' side. <see cref="FileTransfer"/> is the real one.</summary>
public interface IFileExchange
{
    /// <summary>
    /// Makes files and folders of this machine readable for the guest. Returns where the guest finds each one
    /// in the transfer share ("7/report.pdf") and how many could not be offered; null when none could.
    /// </summary>
    (IReadOnlyList<string> Items, int LeftOut)? Offer(IReadOnlyList<string> paths);
    /// <summary>Makes room for files from the guest: the name of the place in the share's inbox, and how many bytes may come. Null when there is no room to be had.</summary>
    (string Id, long Limit)? Receive();
    /// <summary>What the guest left in that place, as full paths on this machine; empty when it left nothing.</summary>
    IReadOnlyList<string> Received(string id);
    /// <summary>The guest will not finish what it was asked for, or what it brought is not wanted any more.</summary>
    void Discard(string id);
}

/// <summary>
/// The share behind file copy and paste, served beside the user's shared folders under the same name and
/// password. The guest mounts it once and keeps it out of sight. It has two sides:
///
/// - Numbered folders, one for each folder of this machine that copied files came from. Such a folder shows
///   the copied files and folders only, not what lies beside them, and nothing in it can be changed. The
///   guest's pasteboard gets the addresses of the files in it, and Finder copies from there when the user
///   pastes: nothing is sent before that, and there is no limit on size.
/// - "in", where the guest's helper puts the files that were copied in macOS, in a folder the broker made for
///   that one time. It takes as many bytes as the <see cref="Budget"/> says, and none while nothing is expected.
///
/// Everything goes through a <see cref="GuardedStore"/>, so a link inside a copied folder that leads out of
/// where the copy came from is refused as it is in a shared folder.
/// </summary>
public sealed class TransferStore : INTFileStore
{
    public const string Inbox = "in";
    /// <summary>Copied files from one folder: the store of that folder, and which names in it the guest may see.</summary>
    public sealed record Picked(INTFileStore Store, IReadOnlySet<string> Names);
    /// <param name="Only">For a numbered folder itself: the names its listing is cut down to.</param>
    sealed record Opened(INTFileStore Store, object Handle, IReadOnlySet<string>? Only);

    readonly INTFileStore top, inbox;
    readonly Dictionary<string, Picked> picked = new(StringComparer.Ordinal);

    /// <param name="folder">A folder of the broker's own, with the inbox in it. Its listing is what the share's top shows.</param>
    public TransferStore(string folder, RateLimit limit, Budget budget, Action<string> log)
    {
        var inboxFolder = Path.Combine(folder, Inbox);
        top = new GuardedStore(new NTDirectoryFileSystem(folder), new PathGuard(folder), true, limit, log);
        inbox = new GuardedStore(new NTDirectoryFileSystem(inboxFolder), new PathGuard(inboxFolder), false, limit, log, budget);
    }

    public void Add(string id, Picked set) { lock (picked) picked[id] = set; }
    /// <summary>What is open stays open; nothing more can be opened in it.</summary>
    public void Remove(string id) { lock (picked) picked.Remove(id); }

    /// <summary>Whether the guest may ask for write access to a path of the share: only below the inbox.</summary>
    public static bool Writable(string path) => path.TrimStart('\\').StartsWith(Inbox + "\\", StringComparison.OrdinalIgnoreCase);

    public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess, SmbAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition, CreateOptions createOptions, SecurityContext securityContext)
    {
        handle = null!; fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
        var whole = path.TrimStart('\\');
        int cut = whole.IndexOf('\\');
        string first = cut < 0 ? whole : whole[..cut], rest = cut < 0 ? "" : whole[(cut + 1)..].TrimStart('\\');
        INTFileStore store; IReadOnlySet<string>? only = null;
        if (first.Length == 0) store = top;
        else if (first.Equals(Inbox, StringComparison.OrdinalIgnoreCase)) store = inbox;
        else
        {
            Picked? set;
            lock (picked) picked.TryGetValue(first, out set);
            if (set == null) return cut < 0 ? NTStatus.STATUS_OBJECT_NAME_NOT_FOUND : NTStatus.STATUS_OBJECT_PATH_NOT_FOUND;
            if (rest.Length == 0) only = set.Names;
            else
            {
                // Directly below the number there is only what was copied, each with its streams.
                int end = rest.IndexOfAny(['\\', ':']);
                if (!set.Names.Contains(end < 0 ? rest : rest[..end])) return rest.Contains('\\') ? NTStatus.STATUS_OBJECT_PATH_NOT_FOUND : NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
            }
            store = set.Store;
        }
        var status = store.CreateFile(out var inner, out fileStatus, rest, desiredAccess, fileAttributes, shareAccess, createDisposition, createOptions, securityContext);
        if (status == NTStatus.STATUS_SUCCESS) handle = new Opened(store, inner, only);
        return status;
    }

    public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName, FileInformationClass informationClass)
    {
        result = null!;
        if (handle is not Opened open) return NTStatus.STATUS_INVALID_HANDLE;
        var status = open.Store.QueryDirectory(out result, open.Handle, fileName, informationClass);
        if (status == NTStatus.STATUS_SUCCESS && open.Only is { } names) result.RemoveAll(entry => Name(entry) is not { } name || !(name is "." or ".." || names.Contains(name)));
        return status;
    }
    static string? Name(QueryDirectoryFileInformation entry) => entry switch
    {
        FileIdBothDirectoryInformation i => i.FileName, FileBothDirectoryInformation i => i.FileName, FileIdFullDirectoryInformation i => i.FileName,
        FileFullDirectoryInformation i => i.FileName, FileDirectoryInformation i => i.FileName, FileNamesInformation i => i.FileName, _ => null
    };

    public NTStatus SetFileInformation(object handle, FileInformation information)
    {
        // A new name is a path of the share, which no store behind it would understand; the helper renames nothing.
        if (information is FileRenameInformationType2 or FileRenameInformationType1 or FileLinkInformationType2 or FileLinkInformationType1) return NTStatus.STATUS_NOT_SUPPORTED;
        return handle is Opened open ? open.Store.SetFileInformation(open.Handle, information) : NTStatus.STATUS_INVALID_HANDLE;
    }

    // A watch on a numbered folder would tell of every file beside the copied ones. macOS does without.
    public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context) { ioRequest = null!; return NTStatus.STATUS_NOT_SUPPORTED; }
    public NTStatus Cancel(object ioRequest) => NTStatus.STATUS_NOT_SUPPORTED;
    public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_NOT_SUPPORTED;
    public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass) => top.GetFileSystemInformation(out result, informationClass);

    // Every handle is checked for being one of this store's: the server passes on what the guest sends, a control
    // request that names no file among it (macOS sends some when it connects), and an exception here would be
    // the end of the broker.
    public NTStatus CloseFile(object handle) => handle is Opened open ? open.Store.CloseFile(open.Handle) : NTStatus.STATUS_INVALID_HANDLE;
    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount) { data = null!; return handle is Opened open ? open.Store.ReadFile(out data, open.Handle, offset, maxCount) : NTStatus.STATUS_INVALID_HANDLE; }
    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data) { numberOfBytesWritten = 0; return handle is Opened open ? open.Store.WriteFile(out numberOfBytesWritten, open.Handle, offset, data) : NTStatus.STATUS_INVALID_HANDLE; }
    public NTStatus FlushFileBuffers(object handle) => handle is Opened open ? open.Store.FlushFileBuffers(open.Handle) : NTStatus.STATUS_INVALID_HANDLE;
    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) => handle is Opened open ? open.Store.LockFile(open.Handle, byteOffset, length, exclusiveLock) : NTStatus.STATUS_INVALID_HANDLE;
    public NTStatus UnlockFile(object handle, long byteOffset, long length) => handle is Opened open ? open.Store.UnlockFile(open.Handle, byteOffset, length) : NTStatus.STATUS_INVALID_HANDLE;
    public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass) { result = null!; return handle is Opened open ? open.Store.GetFileInformation(out result, open.Handle, informationClass) : NTStatus.STATUS_INVALID_HANDLE; }
    public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation) { result = null!; return handle is Opened open ? open.Store.GetSecurityInformation(out result, open.Handle, securityInformation) : NTStatus.STATUS_INVALID_HANDLE; }
    public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor) => handle is Opened open ? open.Store.SetSecurityInformation(open.Handle, securityInformation, securityDescriptor) : NTStatus.STATUS_INVALID_HANDLE;
    public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength) { output = null!; return handle is Opened open ? open.Store.DeviceIOControl(open.Handle, ctlCode, input, out output, maxOutputLength) : NTStatus.STATUS_NOT_SUPPORTED; }
}

/// <summary>
/// The files that cross with the clipboard, on this machine's side.
///
/// Windows to macOS costs nothing until the user pastes: the copied files are made readable in the
/// <see cref="TransferStore"/> and the guest is told where they are. The last few copies stay readable, so that
/// a paste that is still running in macOS is not cut off by the next copy in Windows.
///
/// macOS to Windows cannot wait for the paste: Explorer wants files that are there. So when the VM's window
/// is left with files on the guest's pasteboard, the helper copies them into the inbox, and only then are they
/// put on the Windows clipboard. One such delivery is kept, the latest; the folder is emptied when the broker
/// starts and when it stops.
/// </summary>
public sealed class FileTransfer : IFileExchange
{
    public const string Capability = "files";
    /// <summary>Most that one copy in macOS brings over. Leaving the VM's window with files on the pasteboard starts the transfer unasked, so it has a size.</summary>
    public const long InboundLimit = 256L * 1024 * 1024;
    /// <summary>How many copies made in Windows stay readable, and from how many folders one copy may come.</summary>
    const int Kept = 3, Sources = 8;

    readonly string folder, inbox;
    readonly RateLimit limit;
    readonly Action<string> log;
    readonly Budget budget = new();
    readonly Queue<List<string>> offered = new();
    int next;
    string? arrived;
    public TransferStore Store { get; }

    /// <param name="folder">A folder for this alone: whatever is in it is removed.</param>
    public FileTransfer(string folder, RateLimit limit, Action<string> log)
    {
        this.folder = folder; this.limit = limit; this.log = log;
        inbox = Path.Combine(folder, TransferStore.Inbox);
        Remove(folder);
        Directory.CreateDirectory(inbox);
        Store = new TransferStore(folder, limit, budget, log);
    }

    public (IReadOnlyList<string> Items, int LeftOut)? Offer(IReadOnlyList<string> paths)
    {
        var sources = new List<(string Folder, HashSet<string> Names)>(); int leftOut = 0;
        foreach (var path in paths)
        {
            // A whole drive has no folder it lies in, and a network path is not ours to hand on.
            string? parent = ShareConfig.PathProblem(path) == null ? Path.GetDirectoryName(path) : null;
            var source = parent == null ? default : sources.FirstOrDefault(s => s.Folder.Equals(parent, StringComparison.OrdinalIgnoreCase));
            if (parent != null && source.Names == null && sources.Count < Sources) sources.Add(source = (parent, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
            if (source.Names == null) { leftOut++; continue; }
            source.Names.Add(Path.GetFileName(path));
        }

        var ids = new List<string>(); var items = new List<string>(); int bytes = 0;
        foreach (var (parent, names) in sources)
        {
            INTFileStore store;
            try { store = new GuardedStore(new NTDirectoryFileSystem(parent), new PathGuard(parent), true, limit, log); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { leftOut += names.Count; continue; }
            var id = (++next).ToString();
            // The folder of that name is only what the share's top lists; what is opened under it is the copied files.
            try { Directory.CreateDirectory(Path.Combine(folder, id)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Store.Add(id, new TransferStore.Picked(store, names)); ids.Add(id);
            foreach (var name in names)
            {
                // The list travels as one frame's body.
                var item = id + "/" + name; int size = Encoding.UTF8.GetByteCount(item) + 1;
                if (bytes + size > ClipboardSync.TextLimit) { leftOut++; continue; }
                bytes += size; items.Add(item);
            }
        }
        if (items.Count == 0)
        {
            foreach (var id in ids) Forget(id);
            return null;
        }
        offered.Enqueue(ids);
        while (offered.Count > Kept) foreach (var id in offered.Dequeue()) Forget(id);
        return (items, leftOut);
    }
    void Forget(string id) { Store.Remove(id); Remove(Path.Combine(folder, id)); }

    public (string Id, long Limit)? Receive()
    {
        try
        {
            Sweep(arrived);
            var id = (++next).ToString();
            Directory.CreateDirectory(Path.Combine(inbox, id));
            // The helper counts before it copies. The eighth on top is for what it cannot count exactly.
            budget.Reset(InboundLimit + InboundLimit / 8);
            return (id, InboundLimit);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("file transfer: no place for files from the guest: " + e.Message); return null; }
    }

    public IReadOnlyList<string> Received(string id)
    {
        budget.Reset(0);
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(Path.Combine(inbox, id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { entries = []; }
        if (entries.Length == 0) { Remove(Path.Combine(inbox, id)); return []; }
        arrived = id;
        Sweep(id);
        return entries;
    }

    public void Discard(string id)
    {
        budget.Reset(0);
        if (arrived == id) arrived = null;
        Remove(Path.Combine(inbox, id));
    }

    /// <summary>When the broker stops: nothing of the guest's stays behind, and the Windows clipboard may then name files that are gone.</summary>
    public void Clear() { budget.Reset(0); Remove(folder); }

    /// <summary>Empties the inbox but for one delivery.</summary>
    void Sweep(string? keep)
    {
        try { foreach (var entry in Directory.GetFileSystemEntries(inbox)) if (Path.GetFileName(entry) != keep) Remove(entry); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Removes a file or a folder with what is in it. What Explorer is still copying from stays until a later sweep.</summary>
    static void Remove(string path)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The guest can mark what it wrote read-only, and Windows then refuses to delete it.
                try { foreach (var entry in Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories) : [path]) File.SetAttributes(entry, System.IO.FileAttributes.Normal); }
                catch (Exception again) when (again is IOException or UnauthorizedAccessException) { return; }
            }
        }
    }
}
