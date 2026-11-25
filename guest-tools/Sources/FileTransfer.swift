// SPDX-License-Identifier: GPL-2.0-or-later
import Foundation

/// Brings files that were copied in macOS to the host, so that they can be pasted in Windows.
///
/// Explorer pastes files that exist, so they have to be on the host before the paste. The host asks for
/// them when the VM's window is left with files on the pasteboard, and names a folder in the inbox of
/// its transfer share, which is mounted here like a shared folder. This copies the files there with
/// plain reads and writes, and says when it is done; only then does the host put them on the Windows
/// clipboard. The host removes the folder if the copy fails or is called off, so nothing is cleaned up here.
///
/// Folders are copied with what is in them. Symbolic links are left out: the share has none, and a link
/// would mean nothing on the other side. Names are made into names that Windows accepts.
final class Upload {
    let id: String
    private let sources: [URL]
    private let destination: URL
    private let limit: Int64
    private let lock = NSLock()
    private var stopped = false
    /// Both on the main thread. Progress: bytes written, bytes in all.
    var onProgress: ((Int64, Int64) -> Void)?
    /// Nil when everything arrived; otherwise "cancelled", "too-large", or what went wrong in words.
    var onDone: ((String?) -> Void)?

    /// More files and folders than this in one copy are not brought over.
    static let entryLimit = 20_000
    private static let keys: Set<URLResourceKey> = [.isRegularFileKey, .isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey]

    /// - Parameters:
    ///   - destination: the folder the host made for this one copy.
    ///   - limit: most bytes the host takes; counted before anything is written.
    init(id: String, sources: [URL], destination: URL, limit: Int64) {
        self.id = id
        self.sources = sources
        self.destination = destination
        self.limit = limit
    }

    func start() {
        DispatchQueue.global(qos: .utility).async {
            let problem = self.run()
            DispatchQueue.main.async { self.onDone?(problem) }
        }
    }

    /// The pasteboard changed, the host called it off, or the connection is gone. `onDone` still runs.
    func cancel() {
        lock.lock()
        stopped = true
        lock.unlock()
    }

    private var isStopped: Bool {
        lock.lock()
        defer { lock.unlock() }
        return stopped
    }

    private struct Entry {
        let source: URL
        /// Where it goes, below the destination, in names Windows accepts.
        let target: String
        let folder: Bool
    }

    private func run() -> String? {
        var info = stat()
        guard stat(destination.path, &info) == 0 else {
            // The first use of a network volume makes macOS ask the user; a refusal looks like this.
            return errno == EPERM || errno == EACCES
                ? "macOS does not let Weims Guest Tools use network volumes. Allow it in System Settings › Privacy & Security › Files and Folders."
                : "the host's folder for the files is not there"
        }
        var entries: [Entry] = []
        var total: Int64 = 0
        var skipped = 0
        var taken = Set<String>()
        for source in sources {
            let name = Self.unique(Self.windowsName(source.lastPathComponent), &taken)
            if let problem = plan(source, as: name, &entries, &total, &skipped) { return problem }
        }
        if entries.isEmpty { return "nothing in the copy can be brought over (links are left out)" }
        if skipped > 0 { Log.write("copy to the host: \(skipped) link(s) or special file(s) left out") }

        let size = 1 << 20
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: size, alignment: 16)
        defer { buffer.deallocate() }
        var done: Int64 = 0
        var reported = Date.distantPast
        for entry in entries {
            if isStopped { return "cancelled" }
            let target = destination.path + "/" + entry.target
            if entry.folder {
                if mkdir(target, 0o755) != 0 { return Self.failure(entry.source, errno) }
                continue
            }
            let input = open(entry.source.path, O_RDONLY)
            guard input >= 0 else { return Self.failure(entry.source, errno) }
            let output = open(target, O_WRONLY | O_CREAT | O_EXCL, 0o644)
            guard output >= 0 else {
                let code = errno
                close(input)
                return Self.failure(entry.source, code)
            }
            var problem: String?
            copying: while true {
                if isStopped { problem = "cancelled"; break }
                let got = read(input, buffer, size)
                if got < 0 && errno == EINTR { continue }
                if got < 0 { problem = Self.failure(entry.source, errno); break }
                if got == 0 { break }
                var written = 0
                while written < got {
                    let count = write(output, buffer + written, got - written)
                    if count < 0 && errno == EINTR { continue }
                    if count <= 0 { problem = Self.failure(entry.source, errno); break copying }
                    written += count
                }
                done += Int64(got)
                // A file that grew since it was counted.
                if done > limit { problem = "too-large"; break }
                if Date().timeIntervalSince(reported) >= 0.5 {
                    reported = Date()
                    let (now, all) = (done, total)
                    DispatchQueue.main.async { self.onProgress?(now, all) }
                }
            }
            close(input)
            // What the server refused of the last writes shows up here.
            if close(output) != 0, problem == nil { problem = Self.failure(entry.source, errno) }
            if let problem = problem { return problem }
        }
        return isStopped ? "cancelled" : nil
    }

    /// Lists a file, or a folder and everything in it, before anything is copied: the host takes the copy whole or not at all.
    private func plan(_ url: URL, as target: String, _ entries: inout [Entry], _ total: inout Int64, _ skipped: inout Int) -> String? {
        if isStopped { return "cancelled" }
        guard let values = try? url.resourceValues(forKeys: Self.keys) else { return Self.failure(url, errno) }
        if values.isSymbolicLink == true {
            skipped += 1
        } else if values.isDirectory == true {
            entries.append(Entry(source: url, target: target, folder: true))
            let children: [URL]
            do { children = try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: Array(Self.keys)) }
            catch { return "\(url.lastPathComponent): \(error.localizedDescription)" }
            var taken = Set<String>()
            // Finder's own notes about a folder mean nothing in Windows.
            for child in children.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) where child.lastPathComponent != ".DS_Store" {
                let name = Self.unique(Self.windowsName(child.lastPathComponent), &taken)
                if let problem = plan(child, as: target + "/" + name, &entries, &total, &skipped) { return problem }
            }
        } else if values.isRegularFile == true {
            total += Int64(values.fileSize ?? 0)
            entries.append(Entry(source: url, target: target, folder: false))
        } else {
            skipped += 1
        }
        if total > limit { return "too-large" }
        if entries.count > Self.entryLimit { return "more than \(Self.entryLimit) files and folders in one copy" }
        return nil
    }

    private static func failure(_ url: URL, _ code: Int32) -> String {
        "\(url.lastPathComponent): \(String(cString: strerror(code)))"
    }

    /// A name Windows accepts for a name macOS allows: none of \ / : * ? " < > | and no control character,
    /// no dot or space at the end, and not one of the device names. What Finder shows as a slash is a
    /// colon here, and becomes an underscore like the rest.
    static func windowsName(_ name: String) -> String {
        let refused = Set("\\/:*?\"<>|".unicodeScalars)
        var scalars = String.UnicodeScalarView()
        for scalar in name.unicodeScalars { scalars.append(scalar.value < 32 || refused.contains(scalar) ? "_" : scalar) }
        var out = String(scalars)
        while out.hasSuffix(".") || out.hasSuffix(" ") { out.removeLast() }
        let stem = out.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false).first.map { $0.uppercased() } ?? ""
        let devices = ["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] }
        if out.isEmpty || devices.contains(stem) { out = "_" + out }
        return out
    }

    /// The name, or the name with a number, when another entry of the folder took it already. Windows does not tell names apart by case.
    static func unique(_ name: String, _ taken: inout Set<String>) -> String {
        var candidate = name
        var number = 2
        while !taken.insert(candidate.lowercased()).inserted {
            let whole = name as NSString
            let ending = whole.pathExtension
            candidate = ending.isEmpty ? "\(name) (\(number))" : "\(whole.deletingPathExtension) (\(number)).\(ending)"
            number += 1
        }
        return candidate
    }
}
