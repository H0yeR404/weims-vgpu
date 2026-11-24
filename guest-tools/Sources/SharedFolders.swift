// SPDX-License-Identifier: GPL-2.0-or-later
import Foundation
import NetFS

/// Carries SMB between a port on this Mac's loopback and the host's share server, so that the mounts
/// name this Mac as their server instead of the host's address.
///
/// The reason is what macOS does with a server's address besides using the port it was given. While
/// it mounts, and whenever Finder is asked to show the server, it also tries the standard SMB ports
/// there. At the host's address those ports are not the share server, which has a port of its own, but
/// Windows' own file sharing: every mount then waited 11 to 21 seconds on Windows before it went on, and
/// Finder's entry for the server sat at "Connecting…" for good (measured on macOS 26.7.2). On this
/// Mac's loopback nothing listens on those ports, the attempts are refused at once, and a mount takes
/// a few tenths of a second.
final class Relay {
    /// The host's share port, which changes when the host serves anew; read by the thread that accepts.
    private final class Aim {
        private let lock = NSLock()
        private var port = 0
        func set(_ value: Int) { lock.lock(); port = value; lock.unlock() }
        func get() -> Int { lock.lock(); defer { lock.unlock() }; return port }
    }
    private let aimed = Aim()
    /// The loopback port to mount from.
    let port: Int

    /// Nil when this Mac gives no loopback port to listen on; the folders are then mounted from the host's address.
    init?() {
        // A connection that ends while it is written to must not end the helper.
        signal(SIGPIPE, SIG_IGN)
        // The same port at every start: macOS reconnects a mount by itself, and so finds the relay of a helper
        // that was started again, and the mounts live on. Any free port when another program has that one.
        guard let (listener, port) = Self.open(Self.usualPort) ?? Self.open(0) else { return nil }
        self.port = port
        let aimed = self.aimed
        Thread.detachNewThread {
            while true {
                let inbound = accept(listener, nil, nil)
                if inbound < 0 {
                    // Out of descriptors, or interrupted: neither is a reason to stop accepting for good.
                    if errno != EINTR && errno != ECONNABORTED { usleep(100_000) }
                    continue
                }
                Thread.detachNewThread { Self.carry(inbound, to: aimed.get()) }
            }
        }
    }

    /// Where connections go from now on: the host's share port, or nowhere when there is none.
    func aim(at hostPort: Int) { aimed.set(hostPort > 0 && hostPort <= Int(UInt16.max) ? hostPort : 0) }

    private static let loopback = "127.0.0.1"
    /// One port for each user of this Mac, below the ports the system hands out by itself.
    private static let usualPort = 47000 + Int(getuid() % 1000)

    /// A listening socket on this Mac's loopback, which nothing outside this Mac can connect to, and its port.
    private static func open(_ port: Int) -> (Int32, Int)? {
        let listener = socket(AF_INET, SOCK_STREAM, 0)
        guard listener >= 0 else { return nil }
        // A helper that starts again gets the port although the connections of its last run are not quite gone.
        var on: Int32 = 1
        setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &on, socklen_t(MemoryLayout<Int32>.size))
        var address = Self.address(loopback, port: port)
        var length = socklen_t(MemoryLayout<sockaddr_in>.size)
        let bound = withUnsafePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(listener, $0, length) } }
        let named = withUnsafeMutablePointer(to: &address) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { getsockname(listener, $0, &length) } }
        guard bound == 0, named == 0, listen(listener, 16) == 0, address.sin_port != 0 else {
            close(listener)
            return nil
        }
        return (listener, Int(UInt16(bigEndian: address.sin_port)))
    }

    private static func address(_ host: String, port: Int) -> sockaddr_in {
        var address = sockaddr_in()
        address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        address.sin_family = sa_family_t(AF_INET)
        address.sin_port = UInt16(port).bigEndian
        address.sin_addr.s_addr = inet_addr(host)
        return address
    }

    /// One connection, from its acceptance to its end, on a thread of its own with one more for each direction.
    /// Plain blocking sockets on purpose: with the Network framework's callbacks in between, copying a thousand
    /// small files took half as long again as without a relay (19 s against 13 s); this way it takes a fifth
    /// longer (14.7 s against 11.9 s), and large files go as fast as without. A higher scheduling class for the
    /// two threads made no difference that could be told from the noise.
    private static func carry(_ inbound: Int32, to hostPort: Int) {
        let outbound = hostPort > 0 ? socket(AF_INET, SOCK_STREAM, 0) : -1
        guard outbound >= 0 else { close(inbound); return }
        var host = address(hostAddress, port: hostPort)
        let refused = withUnsafePointer(to: &host) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(outbound, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        guard refused == 0 else {
            close(inbound)
            close(outbound)
            return
        }
        for end in [inbound, outbound] {
            var on: Int32 = 1
            setsockopt(end, IPPROTO_TCP, TCP_NODELAY, &on, socklen_t(MemoryLayout<Int32>.size))
            setsockopt(end, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        }
        let directions = DispatchGroup()
        for (from, to) in [(inbound, outbound), (outbound, inbound)] {
            directions.enter()
            Thread.detachNewThread {
                pump(from, to)
                // Either end closing ends both directions; the other thread's read returns on this.
                shutdown(from, SHUT_RDWR)
                shutdown(to, SHUT_RDWR)
                directions.leave()
            }
        }
        directions.wait()
        close(inbound)
        close(outbound)
    }

    /// One direction. The next piece is read only after the last one was written out, so a slow receiver
    /// slows the sender: that is how the host's upload limit reaches macOS.
    private static func pump(_ from: Int32, _ to: Int32) {
        let size = 256 * 1024
        let buffer = UnsafeMutableRawPointer.allocate(byteCount: size, alignment: 16)
        defer { buffer.deallocate() }
        while true {
            let got = recv(from, buffer, size, 0)
            if got < 0 && errno == EINTR { continue }
            guard got > 0 else { return }
            var written = 0
            while written < got {
                let count = send(to, buffer + written, got - written, 0)
                if count < 0 && errno == EINTR { continue }
                guard count > 0 else { return }
                written += count
            }
        }
    }
}

/// The host's shared folders, mounted the way Finder mounts a server: by the system's mount service,
/// under /Volumes, as this user and without root. The host serves them over SMB on a port beside the
/// control port and sends the name and password for it over the control connection. Neither is
/// stored here, and neither is put into a URL.
///
/// They are mounted through the `Relay` and kept out of Finder's sidebar: there Finder would show
/// their server, and a server's entry leads to the standard SMB ports of its address, which are never
/// the host's share server. The user finds the folders through links in a folder of their home instead
/// (see `linksFolder`) and in the helper's menu.
///
/// The mounts are kept equal to what the host last offered. A mount is ours when it is an SMB mount
/// of this user under a name the host made up ("weims-…"), from the relay or from the host's address;
/// that also finds the mounts an earlier run of the helper left behind.
final class SharedFolders {
    struct Share: Equatable { let name: String; let readOnly: Bool }
    struct Offer: Equatable { var port = 0; var user = ""; var password = ""; var shares: [Share] = [] }
    typealias Mounted = (name: String, path: String)
    typealias Failed = (name: String, error: String)
    private struct Server: Equatable { let host: String; let port: Int }

    /// How long the host keeps serving after the control connection ended. After that a mount only times out.
    static let grace = 30.0
    /// How long a folder that could not be mounted waits for the next attempt.
    static let retryAfter = 60.0
    /// The folder in the user's home that holds a link to each mounted folder. It is made when there is
    /// something to link and then stays, so that the user can keep it in Finder's sidebar.
    static let linksFolder = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Weims Shared Folders", isDirectory: true)
    private static let loopback = "127.0.0.1"
    // The keys and values of NetFS.h, which are C macros and so do not reach Swift.
    private static let uiOption = "UIOption", noUI = "NoUI", allowLoopback = "AllowLoopback", softMount = "SoftMount", mountFlags = "MountFlags"

    /// Mounting waits for the server, so it happens off the main thread, one request after another.
    private let queue = DispatchQueue(label: "io.weims.guest-tools.shares")
    private let relay = Relay()
    // Main thread: the last offer, whether it is still being worked on, and how that went.
    private var current = Offer()
    private var generation = 0
    private var working = false
    private var mounted: [Mounted] = []
    private var failedAt: Date?
    private var leaving: DispatchWorkItem?
    /// Called on the main thread after each change: what is mounted and where, and what could not be mounted and why.
    var onChange: (([Mounted], [Failed]) -> Void)?

    init() {
        if let relay = relay { Log.write("shared folders are mounted through 127.0.0.1:\(relay.port)") }
        else { Log.write("no loopback port for shared folders; they are mounted from the host's address, which is slow") }
    }

    /// What the host offers now: mounts what is missing and unmounts what is no longer offered. Main thread.
    func apply(_ offer: Offer) {
        leaving?.cancel()
        leaving = nil
        current = offer
        generation += 1
        working = true
        let mine = generation
        relay?.aim(at: offer.port)
        let server = relay.map { Server(host: Self.loopback, port: $0.port) } ?? Server(host: hostAddress, port: offer.port)
        queue.async { [weak self] in
            guard let self = self else { return }
            // A newer offer is already waiting behind this one.
            guard DispatchQueue.main.sync(execute: { self.generation }) == mine else { return }
            // Reported after every folder, not once at the end.
            Self.reconcile(offer, from: server) { mounted, failed, done in
                DispatchQueue.main.async {
                    guard self.generation == mine else { return }
                    self.mounted = mounted
                    if done {
                        self.working = false
                        self.failedAt = failed.isEmpty ? nil : Date()
                    }
                    Self.link(mounted)
                    self.onChange?(mounted, failed)
                }
            }
        }
    }

    /// The control connection ended, or there has not been one yet. If the host does not make an offer
    /// within the time it keeps serving, the mounts go: reading from one would block until it times out.
    func hostGone() {
        guard leaving == nil else { return }
        let item = DispatchWorkItem { [weak self] in
            self?.leaving = nil
            self?.apply(Offer())
        }
        leaving = item
        DispatchQueue.main.asyncAfter(deadline: .now() + Self.grace, execute: item)
    }

    /// Before the helper goes: lets go of the mounts while the relay still carries them, and of the links.
    /// Left behind, they would be mounts without a server until macOS gives up on them. Main thread.
    /// When launchd takes the helper out of the session (an uninstall, a new installation over this one), macOS
    /// refuses the unmount with "Operation not permitted": the uninstaller unmounts by itself, and a helper
    /// installed over this one finds the mounts and goes on with them.
    func end() {
        leaving?.cancel()
        leaving = nil
        generation += 1
        current = Offer()
        Self.discard(Self.ours().map { $0.path })
        Self.link([])
    }

    /// To be called every few seconds while the host is connected. A folder that was mounted and is gone,
    /// because macOS let go of it or somebody unmounted it, is mounted again; one that could not be mounted
    /// is tried again after a while.
    func check() {
        guard !current.shares.isEmpty, !working, leaving == nil else { return }
        let offer = current, expected = mounted.map { $0.path }, mine = generation
        let retry = failedAt.map { Date().timeIntervalSince($0) >= Self.retryAfter } ?? false
        queue.async { [weak self] in
            let present = Set(Self.ours().map { $0.path })
            let lost = expected.filter { !present.contains($0) }
            guard !lost.isEmpty || retry else { return }
            DispatchQueue.main.async {
                guard let self = self, self.generation == mine, !self.working else { return }
                if !lost.isEmpty { Log.write("no longer mounted: \(lost.joined(separator: ", ")); mounting again") }
                self.apply(offer)
            }
        }
    }

    private static func reconcile(_ offer: Offer, from server: Server, report: ([Mounted], [Failed], Bool) -> Void) {
        var mounted: [Mounted] = []
        var failed: [Failed] = []
        var stale: [String] = []
        for mount in ours() {
            // Kept only when it is hidden from Finder, comes from where mounts are made now and is under the name
            // the host uses now: not one that a helper before the relay made, and not one of an earlier time of serving.
            let offered = mount.hidden && mount.server == server && mount.user == offer.user && offer.shares.contains { $0.name == mount.name }
            if offered, !mounted.contains(where: { $0.name == mount.name }) { mounted.append((mount.name, mount.path)) } else { stale.append(mount.path) }
        }
        discard(stale)
        let wanted = offer.shares.filter { share in !mounted.contains { $0.name == share.name } }
        report(mounted, failed, wanted.isEmpty)
        // One after another: two requests at once took the mount service longer than two in a row.
        for (index, share) in wanted.enumerated() {
            let started = Date()
            let (path, problem) = mount(share, offer, from: server)
            let seconds = String(format: "%.1f", Date().timeIntervalSince(started))
            if let path = path {
                mounted.append((share.name, path))
                Log.write("mounted shared folder \"\(share.name)\"\(share.readOnly ? " read-only" : "") at \(path) in \(seconds) s")
            } else {
                failed.append((share.name, problem))
                Log.write("could not mount shared folder \"\(share.name)\" after \(seconds) s: \(problem)")
            }
            report(mounted, failed, index == wanted.count - 1)
        }
    }

    private static var leavingPaths = Set<String>()
    private static let leavingLock = NSLock()

    /// Unmounts by force, each mount on a thread of its own, and returns when they are gone or after ten
    /// seconds. Forced because the server behind them is gone or serves something else by now, so there is
    /// nothing to wait for. Waited for that long because a mount on its way out keeps its place under /Volumes
    /// and the new one of that name is then given another ("Work-1"); they usually go within six seconds. Not
    /// waited for to the end because macOS once took 48 seconds to let go of a mount whose server no longer
    /// listened, and the folders that are offered now should not wait for that.
    private static func discard(_ paths: [String]) {
        let gone = DispatchGroup()
        for path in paths {
            leavingLock.lock()
            let first = leavingPaths.insert(path).inserted
            leavingLock.unlock()
            guard first else { continue }
            gone.enter()
            Thread.detachNewThread {
                // macOS lets go of a soft mount by itself once its server is gone; then there is nothing left to unmount.
                if unmount(path, MNT_FORCE) == 0 { Log.write("unmounted \(path)") }
                else if errno != ENOENT && errno != EINVAL { Log.write("could not unmount \(path): \(String(cString: strerror(errno)))") }
                leavingLock.lock()
                leavingPaths.remove(path)
                leavingLock.unlock()
                gone.leave()
            }
        }
        _ = gone.wait(timeout: .now() + 10)
    }

    private static func mount(_ share: Share, _ offer: Offer, from server: Server) -> (path: String?, problem: String) {
        var address = URLComponents()
        address.scheme = "smb"
        address.host = server.host
        address.port = server.port
        address.path = "/" + share.name
        guard let url = address.url else { return (nil, "the name cannot be put into an address") }
        // Never ask the user for anything: the name and password come from the host. The server is this Mac's
        // loopback when the relay is in use, which the mount service wants to be told is meant.
        let open: NSMutableDictionary = [uiOption: noUI, allowLoopback: true]
        // Soft: when the host stops answering, reads and writes fail after a timeout instead of hanging for good.
        // Not for browsing: Finder then leaves the mount, and with it its server, out of the sidebar.
        let options: NSMutableDictionary = [softMount: true, mountFlags: Int(MNT_DONTBROWSE | (share.readOnly ? MNT_RDONLY : 0))]
        var points: Unmanaged<CFArray>?
        let status = NetFSMountURLSync(url as CFURL, nil, offer.user as CFString, offer.password as CFString,
                                       open as CFMutableDictionary, options as CFMutableDictionary, &points)
        let paths = points?.takeRetainedValue() as? [String] ?? []
        if (status == 0 || status == EEXIST), let path = paths.first { return (path, "") }
        if status > 0 { return (nil, "\(String(cString: strerror(status))) (\(status))") }
        return (nil, "the mount service answered \(status)")
    }

    /// Makes the folder of links say what is mounted: a link for each mounted folder, and none of ours that
    /// leads to a folder that is no longer mounted. Only links into /Volumes are ever removed or replaced;
    /// whatever else is in the folder is the user's.
    private static func link(_ mounted: [Mounted]) {
        let files = FileManager.default
        if !mounted.isEmpty { try? files.createDirectory(at: linksFolder, withIntermediateDirectories: true) }
        guard let present = try? files.contentsOfDirectory(atPath: linksFolder.path) else { return }
        for name in present {
            let path = linksFolder.appendingPathComponent(name).path
            guard let target = try? files.destinationOfSymbolicLink(atPath: path), target.hasPrefix("/Volumes/") else { continue }
            if !mounted.contains(where: { $0.name == name && $0.path == target }) { try? files.removeItem(atPath: path) }
        }
        for mount in mounted {
            let path = linksFolder.appendingPathComponent(mount.name).path
            // Anything still there under that name is the right link or the user's own.
            if (try? files.attributesOfItem(atPath: path)) != nil { continue }
            do { try files.createSymbolicLink(atPath: path, withDestinationPath: mount.path) }
            catch { Log.write("could not make the link \(path): \(error.localizedDescription)") }
        }
    }

    private struct Existing { let server: Server; let user: String; let name: String; let path: String; let hidden: Bool }

    /// This user's SMB mounts under a name the host made up, from the relay or from the host's address.
    private static func ours() -> [Existing] {
        // Into a buffer of our own, with room for mounts made meanwhile: this is called from more than one thread.
        let room = Int(max(getfsstat(nil, 0, MNT_NOWAIT), 0)) + 8
        let entries = UnsafeMutablePointer<statfs>.allocate(capacity: room)
        defer { entries.deallocate() }
        let count = Int(max(getfsstat(entries, Int32(room * MemoryLayout<statfs>.stride), MNT_NOWAIT), 0))
        var result: [Existing] = []
        for index in 0..<count {
            let entry = entries[index]
            guard entry.f_owner == getuid(), text(entry.f_fstypename) == "smbfs" else { continue }
            // "//weims-1a2b3c4d@127.0.0.1:52011/Work%20Files"
            guard let from = URLComponents(string: "smb:" + text(entry.f_mntfromname)), let host = from.host, host == loopback || host == hostAddress,
                  let user = from.user, user.hasPrefix("weims-"), let port = from.port, from.path.count > 1 else { continue }
            result.append(Existing(server: Server(host: host, port: port), user: user, name: String(from.path.dropFirst()),
                                   path: text(entry.f_mntonname), hidden: entry.f_flags & UInt32(MNT_DONTBROWSE) != 0))
        }
        return result
    }

    /// A fixed-size C string of a system structure, which Swift sees as a tuple of bytes.
    private static func text<T>(_ field: T) -> String {
        withUnsafeBytes(of: field) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
    }
}
