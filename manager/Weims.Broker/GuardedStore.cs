// SPDX-License-Identifier: GPL-2.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SMBLibrary;
using SmbAttributes = SMBLibrary.FileAttributes;

namespace Weims.Broker;

/// <summary>
/// Decides whether a path the guest names inside a shared folder is inside it once Windows has followed
/// every junction and symbolic link on the way. The SMB library hands the guest's path to Windows as it
/// is, and Windows follows links: a junction in the folder that points elsewhere would let the guest read
/// and write there, as a macOS guest did in a test. A link is allowed when it ends inside the folder.
/// </summary>
public sealed class PathGuard
{
    const int NotFound = 2, PathNotFound = 3, InvalidName = 123, NotADirectory = 267;
    readonly string root, realRoot, realRootInside;

    /// <param name="folder">The shared folder, a full path. It must exist.</param>
    public PathGuard(string folder)
    {
        root = folder.Length > 3 ? folder.TrimEnd('\\') : folder;
        realRoot = Real(root).Path ?? throw new DirectoryNotFoundException(Weims.Core.Text.Get("共享文件夹不存在或无法打开：", "The shared folder is missing or cannot be opened: ") + folder);
        realRootInside = realRoot.EndsWith('\\') ? realRoot : realRoot + "\\";
    }

    /// <summary>
    /// Null when the guest may open or create <paramref name="path"/>, given as SMB gives it: relative to the
    /// share, with backslashes, possibly naming a stream of a file. Otherwise the status to answer with.
    /// </summary>
    public NTStatus? Refusal(string path, out string? why)
    {
        why = null;
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            // A colon separates a stream's name from its file's, and only a path's last part names a file.
            int colon = parts[i].IndexOf(':');
            if (parts[i] is "." or ".." || parts[i].Any(c => c < 32 || c == '/') || (colon >= 0 && i < parts.Length - 1)) return NTStatus.STATUS_OBJECT_NAME_INVALID;
            if (colon >= 0) parts[i] = parts[i][..colon];
        }
        if (parts.Length > 0 && parts[^1].Length == 0) parts = parts[..^1];     // a stream of the folder before it

        // The longest part of the path that exists decides: what comes after it does not exist yet, so it is not a link.
        for (int depth = parts.Length; depth >= 0; depth--)
        {
            var prefix = depth == 0 ? root : root.TrimEnd('\\') + "\\" + string.Join('\\', parts, 0, depth);
            var (real, error) = Real(prefix);
            if (real != null)
            {
                if (Inside(real)) return null;
                why = "it leads out of the shared folder";
                return NTStatus.STATUS_ACCESS_DENIED;
            }
            if (error == InvalidName) return NTStatus.STATUS_OBJECT_NAME_INVALID;
            if (error is not (NotFound or PathNotFound or NotADirectory)) { why = "Windows error " + error + " while following it"; return NTStatus.STATUS_ACCESS_DENIED; }
            // Windows found nothing at the end. If the name itself exists, it is a link to something that
            // is not there, and creating a file through such a link would create it wherever the link points.
            if (GetFileAttributesW(Extended(prefix)) != uint.MaxValue) { why = "it goes through a link whose target is missing"; return NTStatus.STATUS_ACCESS_DENIED; }
        }
        why = "the shared folder is gone";
        return NTStatus.STATUS_ACCESS_DENIED;
    }

    /// <summary>Whether an open handle is of something inside the folder. The check after the fact: it closes the gap between looking and opening.</summary>
    public bool Holds(IntPtr handle) => Final(handle) is { } real && Inside(real);

    bool Inside(string real)
    {
        // The name of a stream may follow the file's; the folder's own streams would otherwise not compare equal to it.
        int name = real.LastIndexOf('\\') + 1, colon = real.IndexOf(':', name);
        if (colon >= 0) real = real[..colon];
        return real.Equals(realRoot, StringComparison.OrdinalIgnoreCase) || real.StartsWith(realRootInside, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Where a path really is, links followed, or the Windows error that stopped the search.</summary>
    static (string? Path, int Error) Real(string path)
    {
        // No access is asked for, so nobody's sharing mode can refuse it; the flag lets a directory be opened.
        using var handle = CreateFileW(Extended(path), 0, 7, 0, 3 /* OPEN_EXISTING */, 0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS */, 0);
        if (handle.IsInvalid) return (null, Marshal.GetLastPInvokeError());
        return Final(handle.DangerousGetHandle()) is { } real ? (real, 0) : (null, Marshal.GetLastPInvokeError() is var e && e != 0 ? e : -1);
    }

    static string? Final(IntPtr handle)
    {
        var buffer = new char[512];
        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0) return null;
            if (length < buffer.Length) return new string(buffer, 0, (int)length);
            buffer = new char[length + 1];
        }
        return null;
    }

    /// <summary>The form of a path that Windows takes literally, as the SMB library's own opens do: no "..", no trimming of dots and spaces.</summary>
    static string Extended(string path) => @"\\?\" + path;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern uint GetFinalPathNameByHandleW(IntPtr handle, [Out] char[] path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern uint GetFileAttributesW(string name);
}

/// <summary>
/// The file store a share is served from: the SMB library's own store for a Windows folder, behind the
/// checks the library does not make. Every path is put to the <see cref="PathGuard"/>; a read-only share
/// changes nothing; the guest cannot make links or reach for the volume; and what the guest writes is
/// counted against the upload limit, which slows the guest down (see <see cref="RateLimit"/>).
/// </summary>
public sealed class GuardedStore(INTFileStore inner, PathGuard guard, bool readOnly, RateLimit limit, Action<string> log) : INTFileStore
{
    // Sparse files and their ranges. Everything else that is a file system control is refused: among them are
    // the ones that make and remove links, and whatever a later Windows adds.
    const uint SetSparse = 0x000900C4, QueryAllocatedRanges = 0x000940CF, SetZeroData = 0x000980C8;
    int refusals;

    NTStatus Refused(string path, NTStatus status, string? why)
    {
        // The guest chooses the names; the log is not theirs to fill.
        if (why != null && Interlocked.Increment(ref refusals) <= 20) log($"shared folder: refused \"{Printable(path)}\": {why}");
        return status;
    }
    static string Printable(string text) => new(text.Where(c => !char.IsControl(c)).Take(200).ToArray());

    public NTStatus CreateFile(out object handle, out FileStatus fileStatus, string path, AccessMask desiredAccess, SmbAttributes fileAttributes, ShareAccess shareAccess, CreateDisposition createDisposition, CreateOptions createOptions, SecurityContext securityContext)
    {
        handle = null!; fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
        if (guard.Refusal(path, out var why) is { } refusal) return Refused(path, refusal, why);
        if (readOnly && (createDisposition != CreateDisposition.FILE_OPEN || (createOptions & CreateOptions.FILE_DELETE_ON_CLOSE) != 0)) return NTStatus.STATUS_ACCESS_DENIED;
        var status = inner.CreateFile(out handle, out fileStatus, path, desiredAccess, fileAttributes, shareAccess, createDisposition, createOptions, securityContext);
        if (status != NTStatus.STATUS_SUCCESS || handle is not IntPtr native || guard.Holds(native)) return status;
        inner.CloseFile(handle); handle = null!;
        return Refused(path, NTStatus.STATUS_ACCESS_DENIED, "what was opened is outside the shared folder");
    }

    public NTStatus WriteFile(out int numberOfBytesWritten, object handle, long offset, byte[] data)
    {
        numberOfBytesWritten = 0;
        if (readOnly) return NTStatus.STATUS_ACCESS_DENIED;
        var status = inner.WriteFile(out numberOfBytesWritten, handle, offset, data);
        // The server reads this connection's next request only after this returns, so waiting here is what slows the guest.
        limit.Spend(data.Length);
        return status;
    }

    public NTStatus SetFileInformation(object handle, FileInformation information)
    {
        if (readOnly) return NTStatus.STATUS_ACCESS_DENIED;
        // A rename names its destination by path, which is as much the guest's choice as the path of an open.
        string? destination = information switch { FileRenameInformationType2 r => r.FileName, FileRenameInformationType1 r => r.FileName, _ => null };
        if (destination != null && guard.Refusal(destination, out var why) is { } refusal) return Refused(destination, refusal, why);
        if (information is FileLinkInformationType2 or FileLinkInformationType1) return NTStatus.STATUS_NOT_SUPPORTED;
        return inner.SetFileInformation(handle, information);
    }

    public NTStatus DeviceIOControl(object handle, uint ctlCode, byte[] input, out byte[] output, int maxOutputLength)
    {
        output = null!;
        if (ctlCode != QueryAllocatedRanges && (readOnly || ctlCode is not (SetSparse or SetZeroData))) return NTStatus.STATUS_NOT_SUPPORTED;
        return inner.DeviceIOControl(handle, ctlCode, input, out output, maxOutputLength);
    }

    public NTStatus SetSecurityInformation(object handle, SecurityInformation securityInformation, SecurityDescriptor securityDescriptor)
        => readOnly ? NTStatus.STATUS_ACCESS_DENIED : inner.SetSecurityInformation(handle, securityInformation, securityDescriptor);
    // The volume is the host's, not the share's.
    public NTStatus SetFileSystemInformation(FileSystemInformation information) => NTStatus.STATUS_NOT_SUPPORTED;

    public NTStatus CloseFile(object handle) => inner.CloseFile(handle);
    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount) => inner.ReadFile(out data, handle, offset, maxCount);
    public NTStatus FlushFileBuffers(object handle) => inner.FlushFileBuffers(handle);
    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) => inner.LockFile(handle, byteOffset, length, exclusiveLock);
    public NTStatus UnlockFile(object handle, long byteOffset, long length) => inner.UnlockFile(handle, byteOffset, length);
    public NTStatus QueryDirectory(out List<QueryDirectoryFileInformation> result, object handle, string fileName, FileInformationClass informationClass) => inner.QueryDirectory(out result, handle, fileName, informationClass);
    public NTStatus GetFileInformation(out FileInformation result, object handle, FileInformationClass informationClass) => inner.GetFileInformation(out result, handle, informationClass);
    public NTStatus GetFileSystemInformation(out FileSystemInformation result, FileSystemInformationClass informationClass) => inner.GetFileSystemInformation(out result, informationClass);
    public NTStatus GetSecurityInformation(out SecurityDescriptor result, object handle, SecurityInformation securityInformation) => inner.GetSecurityInformation(out result, handle, securityInformation);
    public NTStatus NotifyChange(out object ioRequest, object handle, NotifyChangeFilter completionFilter, bool watchTree, int outputBufferSize, OnNotifyChangeCompleted onNotifyChangeCompleted, object context) => inner.NotifyChange(out ioRequest, handle, completionFilter, watchTree, outputBufferSize, onNotifyChangeCompleted, context);
    public NTStatus Cancel(object ioRequest) => inner.Cancel(ioRequest);
}
