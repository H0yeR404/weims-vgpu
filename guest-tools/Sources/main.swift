// SPDX-License-Identifier: GPL-2.0-or-later
// Weims Guest Tools: the macOS side of clipboard sharing and shared folders with the Windows host.
//
// It runs as the logged-in user, needs no root and installs nothing into the system. It connects to
// the host's broker at 10.0.2.2 (QEMU's user-mode network maps that to the host's loopback), checks
// the broker's certificate against the fingerprint remembered at pairing, and shows its token.
import DiskArbitration
import IOKit
import AppKit
import CryptoKit
import Foundation
import Network

let helperVersion = "0.2.0"
let protocolVersion = 1
let helperCapabilities = ["clipboard-text", "self-update", "shares"]
let hostAddress = "10.0.2.2"
let portBase: UInt16 = 46400
let portCount: UInt16 = 64
let textLimit = 4 * 1024 * 1024
let preAuthLimit = 64 * 1024
let reloadNotification = Notification.Name("io.weims.guest-tools.reload")
let concealedType = NSPasteboard.PasteboardType("org.nspasteboard.ConcealedType")
let transientType = NSPasteboard.PasteboardType("org.nspasteboard.TransientType")

func L(_ english: String, _ chinese: String) -> String {
    (Locale.preferredLanguages.first ?? "en").hasPrefix("zh") ? chinese : english
}

// MARK: - Log

/// Plain text log in ~/Library/Logs. It never receives the token, a pairing code or clipboard content.
enum Log {
    static let url = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Logs/Weims Guest Tools.log")
    static let stamp: DateFormatter = {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"; f.locale = Locale(identifier: "en_US_POSIX"); return f
    }()
    static func write(_ message: String) {
        let line = Data("\(stamp.string(from: Date())) \(message)\n".utf8)
        if let handle = try? FileHandle(forWritingTo: url) {
            defer { try? handle.close() }
            _ = try? handle.seekToEnd()
            try? handle.write(contentsOf: line)
        } else {
            try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            FileManager.default.createFile(atPath: url.path, contents: line, attributes: [.posixPermissions: 0o600])
        }
    }
}

// MARK: - Pairing storage

/// What pairing leaves behind: where the broker was, which certificate it showed, and the token it issued.
/// It is kept in a file only this user can read, not in the keychain: a helper without a stable
/// signing identity would be asked for the login password on every keychain read after an update.
struct Pairing: Codable {
    var port: UInt16
    var fingerprint: String
    var token: String
}

enum Store {
    static var directory: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Weims Guest Tools", isDirectory: true)
    }
    static var file: URL { directory.appendingPathComponent("pairing.json") }

    static func load() -> Pairing? {
        guard let data = try? Data(contentsOf: file) else { return nil }
        return try? JSONDecoder().decode(Pairing.self, from: data)
    }

    static func save(_ pairing: Pairing) -> Bool {
        do { return writePrivate(try JSONEncoder().encode(pairing), to: file) } catch { return false }
    }

    /// Created with its final permissions, then moved into place, so the content is never readable by others.
    private static func writePrivate(_ data: Data, to destination: URL) -> Bool {
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let temporary = destination.path + ".tmp"
            try? FileManager.default.removeItem(atPath: temporary)
            guard FileManager.default.createFile(atPath: temporary, contents: data, attributes: [.posixPermissions: 0o600]) else { return false }
            return rename(temporary, destination.path) == 0
        } catch { return false }
    }

    // The ticket the installer took from the install disk; see Ticket.
    static var ticketFile: URL { directory.appendingPathComponent("ticket.json") }
    static func loadTicket() -> Ticket? {
        guard let data = try? Data(contentsOf: ticketFile) else { return nil }
        return try? JSONDecoder().decode(Ticket.self, from: data)
    }
    static func saveTicket(_ ticket: Ticket) -> Bool {
        do { return writePrivate(try JSONEncoder().encode(ticket), to: ticketFile) } catch { return false }
    }
    static func removeTicket() { try? FileManager.default.removeItem(at: ticketFile) }
}

/// What the install disk tells the helper: on which port this VM's broker listens, and a one-time
/// code. Weims Manager gives the disk both as its USB serial number, and the installer reads that
/// from the device tree. Reading a file on the disk instead would make macOS ask the user whether
/// the installer may access removable volumes.
///
/// The code is never sent. Helper and broker each prove that they know it, over the certificate the
/// connection really uses, the broker first; so the helper knows whose certificate it is about to
/// remember, and nothing has to be typed or confirmed.
struct Ticket: Codable {
    var port: UInt16
    var code: String
    /// When the installer found it. The host accepts a code for ten minutes.
    var found: Double
    /// The mounted install disk, to be ejected once pairing is done.
    var volume: String?

    var expired: Bool { Date().timeIntervalSince1970 - found > 600 }

    private static let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")

    /// A serial number that is a ticket: 'W', one character for the port, 18 characters of code.
    init?(serial: String, volume: String?) {
        let characters = Array(serial)
        guard characters.count == 20, characters[0] == "W", let offset = Ticket.alphabet.firstIndex(of: characters[1]),
              UInt16(offset) < portCount, characters[2...].allSatisfy({ Ticket.alphabet.contains($0) }) else { return nil }
        port = portBase + UInt16(offset)
        code = String(characters[2...])
        found = Date().timeIntervalSince1970
        self.volume = volume
    }

    /// The serial number of a plugged-in install disk, if it carries a ticket.
    static func serialOfInstallDisk() -> String? {
        var iterator: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOUSBHostDevice"), &iterator) == KERN_SUCCESS else { return nil }
        defer { IOObjectRelease(iterator) }
        var result: String?
        while true {
            let entry = IOIteratorNext(iterator)
            if entry == 0 { break }
            defer { IOObjectRelease(entry) }
            guard let serial = IORegistryEntryCreateCFProperty(entry, "USB Serial Number" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? String,
                  Ticket(serial: serial, volume: nil) != nil else { continue }
            result = serial
        }
        return result
    }
}

// MARK: - Wire format

/// One protocol message inside TLS: u32 length of what follows | u32 header length | JSON header | body.
struct Frame {
    var header: [String: Any]
    var body: Data
    var type: String { header["type"] as? String ?? "" }
}

func encodeFrame(_ header: [String: Any], _ body: Data = Data()) -> Data {
    let json = (try? JSONSerialization.data(withJSONObject: header)) ?? Data("{}".utf8)
    var out = Data(capacity: 8 + json.count + body.count)
    for value in [UInt32(4 + json.count + body.count), UInt32(json.count)] {
        out.append(contentsOf: [UInt8(value >> 24 & 0xff), UInt8(value >> 16 & 0xff), UInt8(value >> 8 & 0xff), UInt8(value & 0xff)])
    }
    out.append(json)
    out.append(body)
    return out
}

func hexString<S: Sequence>(_ bytes: S) -> String where S.Element == UInt8 {
    bytes.map { String(format: "%02x", $0) }.joined()
}

func bytes(hex: String?) -> Data? {
    guard let hex = hex, hex.count % 2 == 0 else { return nil }
    var out = Data(capacity: hex.count / 2)
    var index = hex.startIndex
    while index < hex.endIndex {
        let next = hex.index(index, offsetBy: 2)
        guard let byte = UInt8(hex[index..<next], radix: 16) else { return nil }
        out.append(byte)
        index = next
    }
    return out
}

/// The four digits both sides show while pairing. The broker computes the same from the same inputs.
func pairingDigits(fingerprint: Data, helper: Data, broker: Data) -> String {
    var hash = SHA256()
    hash.update(data: Data("weims-pair-v1".utf8))
    hash.update(data: fingerprint)
    hash.update(data: helper)
    hash.update(data: broker)
    let d = Array(hash.finalize().prefix(4))
    let number = UInt32(d[0]) << 24 | UInt32(d[1]) << 16 | UInt32(d[2]) << 8 | UInt32(d[3])
    return String(format: "%04d", number % 10000)
}

// MARK: - One connection to the broker

/// A TLS connection to one port on the host. Everything runs on the main queue.
final class Link {
    private let connection: NWConnection
    private var closed = false
    let port: UInt16
    /// Largest frame accepted; raised once the broker has accepted us.
    var limit = preAuthLimit
    /// SHA-256 of the certificate the peer presented, lowercase hex.
    private(set) var fingerprint: String?
    var onFrame: ((Frame) -> Void)?
    var onClosed: ((String) -> Void)?

    /// - Parameter accept: decides from the peer certificate's fingerprint whether to go on. Nothing
    ///   else about the certificate is checked: it is self-signed and pinned, not issued by an authority.
    init(port: UInt16, accept: @escaping (String) -> Bool) {
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        self.port = port
        let box = FingerprintBox()
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { _, trust, complete in
            let reference = sec_trust_copy_ref(trust).takeRetainedValue()
            var value: String?
            if let chain = SecTrustCopyCertificateChain(reference) as? [SecCertificate], let leaf = chain.first {
                value = SHA256.hash(data: SecCertificateCopyData(leaf) as Data).map { String(format: "%02x", $0) }.joined()
            }
            box.value = value
            complete(value != nil && accept(value!))
        }, .main)
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        tcp.connectionTimeout = 5
        connection = NWConnection(host: NWEndpoint.Host(hostAddress), port: NWEndpoint.Port(rawValue: port)!, using: NWParameters(tls: tls, tcp: tcp))
        self.box = box
    }
    private let box: FingerprintBox

    func start(ready: @escaping () -> Void) {
        connection.stateUpdateHandler = { [weak self] state in
            guard let self = self else { return }
            switch state {
            case .ready:
                self.fingerprint = self.box.value
                ready()
                self.receive()
            case .failed(let error): self.close("\(error)")
            // Do not wait for the network to change; reconnecting is decided by the client.
            case .waiting(let error): self.close("\(error)")
            default: break
            }
        }
        connection.start(queue: .main)
    }

    func send(_ header: [String: Any], _ body: Data = Data()) {
        guard !closed else { return }
        connection.send(content: encodeFrame(header, body), completion: .contentProcessed { [weak self] error in
            if let error = error { self?.close("\(error)") }
        })
    }

    private func receive() {
        connection.receive(minimumIncompleteLength: 8, maximumLength: 8) { [weak self] data, _, _, error in
            guard let self = self, !self.closed else { return }
            guard let prefix = data, prefix.count == 8 else { self.close(error.map { "\($0)" } ?? "closed by the host"); return }
            let bytes = [UInt8](prefix)
            let total = Int(bytes[0]) << 24 | Int(bytes[1]) << 16 | Int(bytes[2]) << 8 | Int(bytes[3])
            let headerLength = Int(bytes[4]) << 24 | Int(bytes[5]) << 16 | Int(bytes[6]) << 8 | Int(bytes[7])
            // Checked before anything is buffered: the lengths come from the other side.
            guard total >= 4, total <= self.limit, headerLength > 0, headerLength <= total - 4 else { self.close("frame length out of range"); return }
            let rest = total - 4
            self.connection.receive(minimumIncompleteLength: rest, maximumLength: rest) { [weak self] data, _, _, error in
                guard let self = self, !self.closed else { return }
                guard let payload = data, payload.count == rest else { self.close(error.map { "\($0)" } ?? "closed by the host"); return }
                let all = [UInt8](payload)
                guard let header = (try? JSONSerialization.jsonObject(with: Data(all[0..<headerLength]))) as? [String: Any] else { self.close("frame header is not a JSON object"); return }
                self.onFrame?(Frame(header: header, body: Data(all[headerLength...])))
                self.receive()
            }
        }
    }

    func close(_ reason: String) {
        guard !closed else { return }
        closed = true
        connection.stateUpdateHandler = nil
        connection.cancel()
        onClosed?(reason)
    }
}

final class FingerprintBox { var value: String? }

// MARK: - Client

/// Pairing, connecting, and the clipboard rules of the guest side.
///
/// The broker tells the helper whether this VM's window is in the foreground on the host. While it
/// is, a change of the macOS pasteboard is sent at once. While it is not, nothing is sent, but the
/// change is remembered: when the window leaves the foreground the broker asks once for the latest
/// content, which covers "copy in macOS, switch to Windows straight away".
final class Client {
    private enum Phase { case hello, credentials, session }

    private var pairing = Store.load()
    private var link: Link?
    private var phase = Phase.hello
    private var probes: [Link] = []
    private var currentPort: UInt16 = 0
    private var retryDelay = 2.0
    private var retryScheduled = false
    private var suspended = false
    private var active = false
    private var dirty = false
    private var lastCount = NSPasteboard.general.changeCount
    private var lastReceived = Date()
    private(set) var status = "" { didSet { if status != oldValue { Log.write("status: \(status)"); onStatus?(status) } } }
    var onStatus: ((String) -> Void)?
    /// The host's shared folders; see SharedFolders.swift.
    private let folders = SharedFolders()
    var onFolders: (([SharedFolders.Mounted]) -> Void)?
    /// Before the helper goes: the shared folders are unmounted while they still can be.
    func end() { folders.end() }
    var isPaired: Bool { pairing != nil }
    var isPairing: Bool { pairingDone != nil }

    func start() {
        Timer.scheduledTimer(withTimeInterval: 0.25, repeats: true) { [weak self] _ in self?.pollPasteboard() }
        Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in self?.keepAlive() }
        let workspace = NSWorkspace.shared.notificationCenter
        // With several users logged in, only the one in front of the screen may use the clipboard channel.
        workspace.addObserver(forName: NSWorkspace.sessionDidResignActiveNotification, object: nil, queue: .main) { [weak self] _ in self?.suspend(true) }
        workspace.addObserver(forName: NSWorkspace.sessionDidBecomeActiveNotification, object: nil, queue: .main) { [weak self] _ in self?.suspend(false) }
        DistributedNotificationCenter.default().addObserver(forName: reloadNotification, object: nil, queue: .main) { [weak self] _ in self?.reload() }
        folders.onChange = { [weak self] mounted, failed in
            guard let self = self else { return }
            self.onFolders?(mounted)
            guard self.phase == .session, let link = self.link else { return }
            link.send(["type": "shares_state", "mounted": mounted.map { $0.name }, "failed": failed.map { ["name": $0.name, "error": $0.error] }])
        }
        // Mounts an earlier run left behind stay only if the host offers them again in time.
        folders.hostGone()
        Log.write("helper \(helperVersion) started")
        connect()
    }

    private func suspend(_ value: Bool) {
        suspended = value
        if value { drop(); status = L("Paused: another user is using the screen", "已暂停：屏幕由其他用户使用") } else { connect() }
    }

    /// Another instance paired on the command line; pick up what it stored.
    private func reload() {
        pairing = Store.load()
        drop()
        connect()
    }

    private func drop() {
        let old = link
        link = nil
        old?.onClosed = nil
        old?.close("replaced")
        if phase == .session { folders.hostGone() }
        // No link, no session: the keep-alive check must not judge the next connection by the last one.
        phase = .hello
        active = false
        cancelProbes()
        let ticketLink = redeeming
        redeeming = nil
        ticketLink?.onClosed = nil
        ticketLink?.onFrame = nil
        ticketLink?.close("replaced")
    }

    private func cancelProbes() {
        let pending = probes
        probes = []
        for probe in pending { probe.onClosed = nil; probe.close("no longer needed") }
    }

    // MARK: Connecting

    private func connect() {
        guard !suspended, link == nil, probes.isEmpty, pairingDone == nil, redeeming == nil else { return }
        // A ticket from the install disk comes first: the user has just installed from Weims Manager,
        // and whatever pairing is stored may be from before a reset on the host.
        if let ticket = Store.loadTicket() { redeem(ticket); return }
        guard let pairing = pairing else { status = L("Not paired", "未配对"); return }
        status = L("Connecting…", "正在连接…")
        // The broker normally stays on its port, so that is tried first and alone. If nothing with
        // the paired certificate answers there, the broker may have moved: when its port is taken on
        // the host it picks another in a fixed range of 64. Several VMs share the same 10.0.2.2, so
        // it is recognised by its certificate. The rest of the range is probed all at once, because
        // a port nobody listens on is not refused quickly here; each attempt runs into its timeout.
        probe([pairing.port]) { [weak self] in
            guard let self = self, let pairing = self.pairing else { return }
            let rest = (0..<portCount).map { portBase + $0 }.filter { $0 != pairing.port }
            self.probe(rest) { [weak self] in
                // Besides a broker that is not running, the usual cause is macOS itself: until the user allows
                // this app under Local Network, every connection to 10.0.2.2 fails at once.
                self?.status = L("Host not found. Check that integration is on for this VM in Weims Manager, and that Weims Guest Tools is allowed in System Settings › Privacy & Security › Local Network.",
                                 "找不到宿主。请确认已在 Weims Manager 里为此虚拟机开启集成，并在“系统设置 › 隐私与安全性 › 本地网络”里允许 Weims Guest Tools。")
                self?.scheduleRetry()
            }
        }
    }

    /// Connects to every given port at once. The first one that completes TLS with the paired
    /// certificate becomes the session; `none` runs when all of them have failed.
    private func probe(_ ports: [UInt16], none: @escaping () -> Void) {
        guard let pairing = pairing, !suspended else { return }
        var remaining = ports.count
        var started: [Link] = []
        for port in ports {
            let candidate = Link(port: port) { $0 == pairing.fingerprint }
            probes.append(candidate)
            started.append(candidate)
            candidate.onClosed = { [weak self, weak candidate] _ in
                guard let self = self, let candidate = candidate else { return }
                self.probes.removeAll { $0 === candidate }
                remaining -= 1
                if remaining == 0, self.link == nil { none() }
            }
            candidate.start { [weak self, weak candidate] in
                guard let self = self, let candidate = candidate, self.link == nil else { return }
                self.probes.removeAll { $0 === candidate }
                self.cancelProbes()
                self.adopt(candidate, port: port)
            }
        }
        // In case a connection attempt neither succeeds nor reports its timeout. Only the attempts of
        // this round are closed, and only while they are still attempts: a later round reuses the same
        // port numbers, and the one that became the session must be left alone.
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { [weak self] in
            guard let self = self else { return }
            for probe in started where self.probes.contains(where: { $0 === probe }) { probe.close("no answer") }
        }
    }

    private func scheduleRetry() {
        guard !retryScheduled else { return }
        retryScheduled = true
        let delay = retryDelay
        retryDelay = min(retryDelay * 2, 30)
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            self?.retryScheduled = false
            self?.connect()
        }
    }

    /// Makes a connected link the current one and starts the handshake on it.
    private func adopt(_ link: Link, port: UInt16) {
        self.link = link
        currentPort = port
        phase = .hello
        link.onFrame = { [weak self, weak link] frame in if let link = link, self?.link === link { self?.handle(frame, on: link) } }
        link.onClosed = { [weak self, weak link] reason in if let link = link, self?.link === link { self?.closed(reason) } }
        link.send(["type": "hello", "version": protocolVersion, "helper": helperVersion, "capabilities": helperCapabilities])
        DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [weak self, weak link] in
            if let link = link, self?.link === link, self?.phase != .session { link.close("the host did not complete the handshake in time") }
        }
    }

    private func closed(_ reason: String) {
        let wasSession = phase == .session
        link = nil
        phase = .hello
        active = false
        Log.write((wasSession ? "disconnected: " : "handshake failed: ") + reason)
        status = L("Disconnected. Reconnecting…", "连接已断开，正在重连…")
        if wasSession { retryDelay = 2; folders.hostGone() }
        scheduleRetry()
    }

    // MARK: Self-update

    private var updating = false

    /// A newer Weims Manager on the host sends its helper over the authenticated connection.
    /// It is installed only if it is newer and signed by the signer of this running copy; see Updater.
    private func applyUpdate(_ frame: Frame, on link: Link) {
        guard !updating, let version = frame.header["version"] as? String, let digest = frame.header["sha256"] as? String else { return }
        func answer(_ problem: String?) {
            if let problem = problem { Log.write("update to \(version) not installed: \(problem)") }
            link.send(["type": "update_result", "ok": problem == nil, "version": version, "reason": problem ?? ""])
        }
        guard Updater.newer(version, than: helperVersion) else { answer("it is not newer than \(helperVersion)"); return }
        guard hexString(SHA256.hash(data: frame.body)) == digest.lowercased() else { answer("the archive arrived damaged"); return }
        updating = true
        let archive = frame.body
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let problem = Updater.install(archive: archive, version: version)
            DispatchQueue.main.async {
                self?.updating = false
                answer(problem)
                guard problem == nil else { return }
                Log.write("updated from \(helperVersion) to \(version); restarting")
                // Give the answer a moment to leave before launchd replaces this process.
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) { Updater.restart() }
            }
        }
    }

    // MARK: Pairing from the install disk

    enum InstallEvent { case started, paired, failed(String) }
    /// Progress of the pairing that follows an install from the install disk, for the welcome window.
    var onInstall: ((InstallEvent) -> Void)?
    private var redeeming: Link?
    private var redeemAttempts = 0
    private var ejectReport = false

    /// Pairs with the broker on the ticket's port, each side proving that it knows the ticket's code.
    private func redeem(_ ticket: Ticket) {
        if ticket.expired || redeemAttempts >= 40 {
            giveUpTicket(ticket, L("The install disk's pairing has expired. In Weims Manager, click Install Guest Tools again, or pair from this menu.",
                                   "安装盘里的配对信息已过期。请在 Weims Manager 里重新点“安装 Guest Tools”，或从此菜单配对。"))
            return
        }
        if redeemAttempts == 0 { onInstall?(.started) }
        redeemAttempts += 1
        status = L("Pairing with the host…", "正在与宿主配对…")
        let attempt = Link(port: ticket.port) { _ in true }
        redeeming = attempt
        var answered = false
        var verified = false
        var random = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, random.count, &random)
        let mine = Data(random)
        let key = SymmetricKey(data: Data(ticket.code.utf8))
        // What one side sends to prove that it knows the code, bound to the certificate in use and both random values.
        func proof(_ side: String, _ fingerprint: Data, _ theirs: Data) -> Data {
            var message = Data("weims-ticket-v1-\(side)".utf8)
            message.append(fingerprint)
            message.append(mine)
            message.append(theirs)
            return Data(HMAC<SHA256>.authenticationCode(for: message, using: key))
        }
        func refused() {
            answered = true
            redeeming = nil
            attempt.onClosed = nil
            attempt.close("ticket refused")
            giveUpTicket(ticket, L("The host did not accept the install disk's pairing. In Weims Manager, click Install Guest Tools again, or pair from this menu.",
                                   "宿主没有接受安装盘里的配对信息。请在 Weims Manager 里重新点“安装 Guest Tools”，或从此菜单配对。"))
        }
        attempt.onFrame = { [weak self, weak attempt] frame in
            guard let self = self, let attempt = attempt, self.redeeming === attempt else { return }
            switch frame.type {
            case "hello":
                attempt.send(["type": "ticket_begin", "nonce": hexString(mine)])
            case "ticket_challenge":
                // Only this VM's broker can make this proof. Anything else on that port is not trusted with ours.
                guard let fingerprint = bytes(hex: attempt.fingerprint), let theirs = bytes(hex: frame.header["nonce"] as? String), theirs.count == 32,
                      let shown = bytes(hex: frame.header["proof"] as? String), shown == proof("broker", fingerprint, theirs) else { refused(); return }
                verified = true
                attempt.send(["type": "ticket_proof", "proof": hexString(proof("helper", fingerprint, theirs)), "user": NSUserName()])
            case "paired":
                guard verified, let token = frame.header["token"] as? String, let fingerprint = attempt.fingerprint else { refused(); return }
                answered = true
                self.redeeming = nil
                attempt.onClosed = nil
                attempt.close("paired")
                let stored = Pairing(port: ticket.port, fingerprint: fingerprint, token: token)
                guard Store.save(stored) else {
                    self.giveUpTicket(ticket, L("Paired, but the pairing could not be stored.", "配对成功，但无法保存配对信息。")); return
                }
                Store.removeTicket()
                self.pairing = stored
                self.redeemAttempts = 0
                self.retryDelay = 2
                Log.write("paired with the host on port \(ticket.port) through the install disk")
                self.onInstall?(.paired)
                self.eject(ticket.volume, tries: 4)
                self.connect()
            default:
                // The code was used, replaced or has expired.
                refused()
            }
        }
        attempt.onClosed = { [weak self, weak attempt] reason in
            guard let self = self, let attempt = attempt, self.redeeming === attempt, !answered else { return }
            // Not reached yet. On a fresh install macOS first asks the user whether this app may use the
            // local network, and refuses every connection until the answer is Allow. Keep trying while the code is good.
            self.redeeming = nil
            Log.write("the host named by the install disk is not reachable yet: \(reason)")
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in self?.connect() }
        }
        attempt.start { [weak attempt] in
            attempt?.send(["type": "hello", "version": protocolVersion, "helper": helperVersion, "capabilities": helperCapabilities])
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [weak self, weak attempt] in
            if let attempt = attempt, self?.redeeming === attempt { attempt.close("no answer") }
        }
    }

    private func giveUpTicket(_ ticket: Ticket, _ problem: String) {
        Store.removeTicket()
        redeemAttempts = 0
        Log.write("install disk pairing given up: \(problem)")
        onInstall?(.failed(problem))
        eject(ticket.volume, tries: 4)
        connect()
        if pairing == nil { status = problem }
    }

    /// The device behind the place where the install disk is mounted ("disk3s1"), or nil if that
    /// place is not a read-only volume of the install disk's name. This is answered from the mount
    /// table. Looking at any file on the disk itself, or letting diskutil do so on this app's behalf,
    /// makes macOS ask the user whether this app may access removable volumes.
    private func installDiskDevice(_ volume: String) -> String? {
        guard volume.hasPrefix("/Volumes/Weims Guest Tools") else { return nil }
        var list: UnsafeMutablePointer<statfs>?
        let count = getmntinfo(&list, MNT_NOWAIT)
        guard count > 0, let mounts = list else { return nil }
        for index in 0..<Int(count) {
            var mount = mounts[index]
            let path = withUnsafePointer(to: &mount.f_mntonname) {
                $0.withMemoryRebound(to: CChar.self, capacity: Int(MAXPATHLEN)) { String(cString: $0) }
            }
            guard path == volume, mount.f_flags & UInt32(MNT_RDONLY) != 0 else { continue }
            let device = withUnsafePointer(to: &mount.f_mntfromname) {
                $0.withMemoryRebound(to: CChar.self, capacity: Int(MAXPATHLEN)) { String(cString: $0) }
            }
            return device.hasPrefix("/dev/") ? String(device.dropFirst(5)) : nil
        }
        return nil
    }

    private var ejectSession: DASession?
    private var ejectDisk: DADisk?
    private var ejectVolume = ""
    private var ejectTries = 0

    /// Unmounts the install disk so that Weims Manager can unplug it. The request goes to the system's
    /// disk arbitration service, which does the unmounting; this app never touches the volume. The
    /// installer that ran from it may still be exiting, hence the retries; the volume is read-only,
    /// so the forced unmount at the end loses nothing.
    private func eject(_ volume: String?, tries: Int) {
        guard let volume = volume, let device = installDiskDevice(volume),
              let session = DASessionCreate(kCFAllocatorDefault),
              let partition = DADiskCreateFromBSDName(kCFAllocatorDefault, session, device) else { return }
        DASessionSetDispatchQueue(session, DispatchQueue.main)
        let disk = DADiskCopyWholeDisk(partition) ?? partition
        ejectSession = session
        ejectDisk = disk
        ejectVolume = volume
        ejectTries = tries
        let options = kDADiskUnmountOptionWhole | (tries > 0 ? kDADiskUnmountOptionDefault : kDADiskUnmountOptionForce)
        DADiskUnmount(disk, DADiskUnmountOptions(options), { _, dissenter, context in
            guard let context = context else { return }
            Unmanaged<Client>.fromOpaque(context).takeUnretainedValue().unmounted(refused: dissenter != nil)
        }, Unmanaged.passUnretained(self).toOpaque())
    }

    private func unmounted(refused: Bool) {
        let volume = ejectVolume
        let tries = ejectTries
        if !refused, let disk = ejectDisk { DADiskEject(disk, DADiskEjectOptions(kDADiskEjectOptionDefault), nil, nil) }
        ejectDisk = nil
        ejectSession = nil
        if !refused {
            Log.write("install disk ejected")
            ejectReport = true
            reportEject()
        } else if tries > 0 {
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.eject(volume, tries: tries - 1) }
        } else {
            Log.write("the install disk could not be ejected; it stays in until the VM is shut down")
        }
    }

    /// Tells the broker that the install disk is free, once there is a session to tell it on.
    private func reportEject() {
        guard ejectReport, phase == .session, let link = link else { return }
        ejectReport = false
        link.send(["type": "install_media", "ejected": true])
    }

    // MARK: Pairing

    /// One connection that is asking a broker to pair.
    private final class PairAttempt {
        enum Step { case hello, begun, shown }
        let link: Link
        let nonce: Data
        var step = Step.hello
        init(link: Link) {
            self.link = link
            var bytes = [UInt8](repeating: 0, count: 32)
            _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
            nonce = Data(bytes)
        }
    }
    private var attempts: [PairAttempt] = []
    private var pairingDone: ((String?) -> Void)?
    private var pairingRound = 0

    /// Pairs by comparing a number; nothing is typed on either side.
    ///
    /// The user has opened a pairing window in Weims Manager. The helper asks every port of the range
    /// and goes on with the broker that answers. It first sends only the hash of a random value, takes
    /// the broker's random value, then reveals its own. Both sides derive four digits from the
    /// broker's certificate and the two values and show them; the user confirms in Manager that they
    /// are the same. Because each side is fixed before it sees the other's value, a program between
    /// the two, which necessarily has another certificate, cannot make both numbers come out equal.
    /// The certificate is remembered only after that confirmation.
    func pair(onDigits: @escaping (String) -> Void, done: @escaping (String?) -> Void) {
        cancelPairing()
        drop()
        pairingRound += 1
        let round = pairingRound
        pairingDone = done
        status = L("Pairing…", "正在配对…")
        for offset in 0..<portCount {
            let attempt = PairAttempt(link: Link(port: portBase + offset) { _ in true })
            attempts.append(attempt)
            attempt.link.onFrame = { [weak self, weak attempt] frame in if let attempt = attempt { self?.pairing(frame, attempt, onDigits) } }
            attempt.link.onClosed = { [weak self, weak attempt] _ in if let attempt = attempt { self?.pairingClosed(attempt) } }
            attempt.link.start { [weak attempt] in
                attempt?.link.send(["type": "hello", "version": protocolVersion, "helper": helperVersion, "capabilities": helperCapabilities])
            }
        }
        // Ports that neither answer nor fail are given up on, so that "nobody is waiting to pair" can be said.
        DispatchQueue.main.asyncAfter(deadline: .now() + 10) { [weak self] in
            guard let self = self, self.pairingRound == round else { return }
            for attempt in self.attempts where attempt.step != .shown { attempt.link.close("no answer") }
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 180) { [weak self] in
            guard let self = self, self.pairingRound == round else { return }
            self.finishPairing(L("Nobody confirmed the number in Weims Manager in time.", "没有人在 Weims Manager 里及时确认这个数字。"))
        }
    }

    /// The user closed the pairing window in macOS.
    func cancelPairing() {
        guard pairingDone != nil else { return }
        pairingDone = nil
        pairingRound += 1
        endAttempts()
        connect()
    }

    private func endAttempts() {
        let all = attempts
        attempts = []
        for attempt in all { attempt.link.onClosed = nil; attempt.link.onFrame = nil; attempt.link.close("pairing ended") }
    }

    private func finishPairing(_ problem: String?) {
        guard let done = pairingDone else { return }
        pairingDone = nil
        pairingRound += 1
        endAttempts()
        status = problem == nil ? L("Paired", "已配对") : (pairing == nil ? L("Not paired", "未配对") : L("Pairing failed", "配对失败"))
        done(problem)
        connect()
    }

    private func pairing(_ frame: Frame, _ attempt: PairAttempt, _ onDigits: (String) -> Void) {
        switch (attempt.step, frame.type) {
        case (.hello, "hello"):
            attempt.step = .begun
            attempt.link.send(["type": "pair_begin", "commit": hexString(SHA256.hash(data: attempt.nonce)), "user": NSUserName()])
        case (.begun, "pair_nonce"):
            guard let theirs = bytes(hex: frame.header["nonce"] as? String), theirs.count == 32,
                  let fingerprint = bytes(hex: attempt.link.fingerprint) else { attempt.link.close("bad pairing answer"); return }
            // This broker has a pairing window open; go on with it alone.
            for other in attempts where other !== attempt { other.link.onClosed = nil; other.link.onFrame = nil; other.link.close("another host answered") }
            attempts = [attempt]
            attempt.step = .shown
            attempt.link.send(["type": "pair_reveal", "nonce": hexString(attempt.nonce)])
            onDigits(pairingDigits(fingerprint: fingerprint, helper: attempt.nonce, broker: theirs))
        case (.shown, "paired"):
            guard let token = frame.header["token"] as? String, let fingerprint = attempt.link.fingerprint else {
                finishPairing(L("The host sent an incomplete answer.", "宿主的应答不完整。")); return
            }
            let stored = Pairing(port: attempt.link.port, fingerprint: fingerprint, token: token)
            guard Store.save(stored) else { finishPairing(L("Paired, but the pairing could not be stored.", "配对成功，但无法保存配对信息。")); return }
            pairing = stored
            retryDelay = 2
            Log.write("paired with the host on port \(attempt.link.port)")
            finishPairing(nil)
        case (.shown, "error"):
            finishPairing(frame.header["code"] as? String == "pair-declined"
                ? L("In Weims Manager the numbers were marked as different. Nothing was paired.", "Weims Manager 里选择了“不一致”，没有完成配对。")
                : L("Nobody confirmed the number in Weims Manager in time.", "没有人在 Weims Manager 里及时确认这个数字。"))
        default:
            // A broker without an open pairing window, or one that is busy with another request.
            attempt.link.close("not pairing")
        }
    }

    private func pairingClosed(_ attempt: PairAttempt) {
        attempts.removeAll { $0 === attempt }
        if attempt.step == .shown {
            finishPairing(L("The connection to the host was lost before the pairing was confirmed.", "在确认配对之前，与宿主的连接断开了。"))
        } else if attempts.isEmpty {
            finishPairing(L("No Weims Manager is waiting to pair. In Weims Manager on Windows, open this VM's Guest Tools settings and click Pair Guest Tools, then try again. If you did, allow Weims Guest Tools in System Settings › Privacy & Security › Local Network.",
                            "没有 Weims Manager 在等待配对。请先在 Windows 的 Weims Manager 里打开此虚拟机的 Guest Tools 设置，点“配对 Guest Tools”，然后重试。如果已经点过，请在“系统设置 › 隐私与安全性 › 本地网络”里允许 Weims Guest Tools。"))
        }
    }

    // MARK: Frames

    private func handle(_ frame: Frame, on link: Link) {
        lastReceived = Date()
        switch phase {
        case .hello:
            guard frame.type == "hello" else { link.close("unexpected answer to hello"); return }
            phase = .credentials
            link.send(["type": "auth", "token": pairing?.token ?? ""])
        case .credentials:
            if frame.type == "ok" { established(link); return }
            // The token is no longer valid: pairing was reset on the host.
            link.onClosed = nil
            link.close("token refused")
            self.link = nil
            phase = .hello
            status = L("The host no longer accepts this pairing. Pair again.", "宿主不再接受此配对，请重新配对。")
        case .session:
            session(frame, on: link)
        }
    }

    private func established(_ link: Link) {
        phase = .session
        link.limit = textLimit + preAuthLimit + 4
        retryDelay = 2
        if var pairing = pairing, pairing.port != currentPort {
            pairing.port = currentPort
            self.pairing = pairing
            _ = Store.save(pairing)
        }
        status = L("Connected", "已连接")
        reportEject()
    }

    private func session(_ frame: Frame, on link: Link) {
        switch frame.type {
        case "focus":
            active = frame.header["active"] as? Bool ?? false
            Log.write("VM window in the foreground on the host: \(active)")
        case "clipboard":
            guard frame.header["kind"] as? String == "text", frame.body.count <= textLimit,
                  let text = String(data: frame.body, encoding: .utf8), !text.isEmpty else { return }
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setString(text, forType: .string)
            // Password managers on the host mark their content; keep it out of clipboard managers here too.
            if frame.header["sensitive"] as? Bool == true { pasteboard.setData(Data(), forType: concealedType) }
            // Our own write must not be sent back.
            lastCount = pasteboard.changeCount
            dirty = false
            Log.write("received text from the host: \(frame.body.count) bytes")
        case "update":
            applyUpdate(frame, on: link)
        case "shares":
            var offer = SharedFolders.Offer()
            for entry in frame.header["shares"] as? [[String: Any]] ?? [] {
                // The name becomes part of an address and of a path under /Volumes.
                guard let name = entry["name"] as? String, !name.isEmpty, name.count <= 64, !name.hasPrefix("."), !name.contains("/"),
                      !name.unicodeScalars.contains(where: { $0.value < 32 }) else { continue }
                offer.shares.append(SharedFolders.Share(name: name, readOnly: entry["read_only"] as? Bool ?? true))
            }
            if !offer.shares.isEmpty {
                guard let port = frame.header["port"] as? Int, port >= Int(portBase), port < Int(portBase + portCount),
                      let user = frame.header["user"] as? String, user.hasPrefix("weims-"),
                      let password = frame.header["password"] as? String else { Log.write("the host's offer of shared folders is incomplete"); return }
                offer.port = port
                offer.user = user
                offer.password = password
            }
            Log.write("the host offers \(offer.shares.count) shared folder(s)")
            folders.apply(offer)
        case "clipboard_request":
            Log.write("the host asked for the latest pasteboard content; changed since last sent: \(dirty)")
            sendPasteboard(to: link, replyTo: frame.header["id"])
        default:
            break
        }
    }

    // MARK: Pasteboard

    /// macOS has no change notification for the pasteboard; the change count is cheap to read.
    private func pollPasteboard() {
        let count = NSPasteboard.general.changeCount
        guard count != lastCount else { return }
        lastCount = count
        dirty = true
        if active, phase == .session, let link = link { sendPasteboard(to: link, replyTo: nil) }
    }

    private func sendPasteboard(to link: Link, replyTo: Any?) {
        let pasteboard = NSPasteboard.general
        var header: [String: Any] = ["type": "clipboard", "kind": "text"]
        if let replyTo = replyTo { header["reply_to"] = replyTo }
        guard dirty, let text = pasteboard.string(forType: .string), !text.isEmpty else {
            dirty = false
            if let replyTo = replyTo { link.send(["type": "clipboard_none", "reply_to": replyTo]) }
            return
        }
        dirty = false
        let body = Data(text.utf8)
        guard body.count <= textLimit else {
            Log.write("pasteboard text over 4 MiB was not sent")
            if let replyTo = replyTo { link.send(["type": "clipboard_none", "reply_to": replyTo]) }
            return
        }
        let types = pasteboard.types ?? []
        if types.contains(concealedType) || types.contains(transientType) { header["sensitive"] = true }
        Log.write("sent text to the host: \(body.count) bytes")
        link.send(header, body)
    }

    private func keepAlive() {
        guard phase == .session, let link = link else { return }
        if Date().timeIntervalSince(lastReceived) > 45 { link.close("the host stopped answering"); return }
        link.send(["type": "ping"])
        folders.check()
    }
}

// MARK: - Menu bar

final class AppDelegate: NSObject, NSApplicationDelegate {
    let client = Client()
    var item: NSStatusItem!
    let statusLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")

    func applicationDidFinishLaunching(_ notification: Notification) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = Self.menuBarIcon()
        let menu = NSMenu()
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())
        menu.addItem(withTitle: L("Pair with Host…", "与宿主配对…"), action: #selector(pair), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: L("Quit Weims Guest Tools", "退出 Weims Guest Tools"), action: #selector(quit), keyEquivalent: "q").target = self
        item.menu = menu
        client.onStatus = { [weak self] text in self?.statusLine.title = text }
        client.onFolders = { [weak self] mounts in self?.showFolders(mounts) }
        // After an install from the install disk the helper pairs by itself; say so, because the first
        // thing macOS shows is its own question about the local network.
        client.onInstall = { [weak self] event in
            guard let self = self else { return }
            let close = L("Close", "关闭")
            self.panel.onClose = nil
            switch event {
            case .started:
                self.panel.show(title: L("Weims Guest Tools is installed", "Weims Guest Tools 已安装"), digits: "",
                                detail: L("Pairing with Weims Manager on Windows… If macOS asks whether Weims Guest Tools may find devices on your local network, click Allow: that is how it reaches Windows.",
                                          "正在与 Windows 的 Weims Manager 配对…如果 macOS 询问是否允许 Weims Guest Tools 查找本地网络上的设备，请点“允许”：它要靠这个连接 Windows。"), button: close)
            case .paired:
                self.panel.show(title: L("Paired", "配对成功"), digits: "",
                                detail: L("The clipboard is now shared with Windows while this VM's window is in front. Weims Guest Tools lives in the menu bar.",
                                          "现在当此虚拟机的窗口在前台时，剪贴板会与 Windows 共享。Weims Guest Tools 在菜单栏里。"), button: close)
                DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in if self?.client.isPairing == false { self?.panel.hide() } }
            case .failed(let problem):
                self.panel.show(title: L("Installed, but not paired", "已安装，但未配对"), digits: "", detail: problem, button: close)
            }
        }
        // launchd ends the helper with this signal, for an uninstall or a new version. The shared folders are let
        // go of first, as when the user quits.
        signal(SIGTERM, SIG_IGN)
        let leave = DispatchSource.makeSignalSource(signal: SIGTERM, queue: .main)
        leave.setEventHandler { [weak self] in
            self?.client.end()
            exit(0)
        }
        leave.resume()
        termination = leave
        client.start()
        statusLine.title = client.status
    }

    /// The Weims mark in one colour: a rounded square with the W left out of it. A template image, so macOS
    /// draws it dark on a light menu bar and light on a dark one. The W's line is heavier than in the coloured
    /// mark, where it would be under a point wide at this size.
    static func menuBarIcon() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18), flipped: true) { bounds in
            let square = bounds.insetBy(dx: 1, dy: 1)
            NSColor.black.setFill()
            NSBezierPath(roundedRect: square, xRadius: square.width * 0.24, yRadius: square.width * 0.24).fill()
            let letter = NSBezierPath()
            let strokes: [(x: CGFloat, y: CGFloat)] = [(0.22, 0.33), (0.35, 0.72), (0.5, 0.45), (0.65, 0.72), (0.78, 0.33)]
            for (index, point) in strokes.enumerated() {
                let at = NSPoint(x: square.minX + point.x * square.width, y: square.minY + point.y * square.height)
                if index == 0 { letter.move(to: at) } else { letter.line(to: at) }
            }
            letter.lineWidth = 1.4
            letter.lineCapStyle = .round
            letter.lineJoinStyle = .round
            // Taken out of the square, not drawn onto it: a template image has one colour and its absence.
            NSGraphicsContext.current?.compositingOperation = .destinationOut
            NSColor.black.setStroke()
            letter.stroke()
            return true
        }
        image.isTemplate = true
        image.accessibilityDescription = "Weims Guest Tools"
        return image
    }

    let panel = PairingPanel()

    @objc func pair() {
        let cancel = L("Cancel", "取消")
        panel.onClose = { [weak self] in self?.client.cancelPairing() }
        panel.show(title: L("Looking for Weims Manager…", "正在寻找 Weims Manager…"), digits: "",
                   detail: L("In Weims Manager on Windows, open this VM's Guest Tools settings and click Pair Guest Tools.", "请在 Windows 的 Weims Manager 里打开此虚拟机的 Guest Tools 设置，点“配对 Guest Tools”。"), button: cancel)
        client.pair(onDigits: { [weak self] digits in
            self?.panel.show(title: L("Compare this number", "请核对这个数字"), digits: digits,
                             detail: L("Weims Manager on Windows shows a number too. If it is the same, click “They match” there. If it is different, click “They differ”.", "Windows 的 Weims Manager 里也显示着一个数字。如果相同，请在那边点“数字一致”；如果不同，请点“不一致”。"), button: cancel)
        }, done: { [weak self] problem in
            guard let self = self else { return }
            self.panel.onClose = nil
            if let problem = problem {
                self.panel.show(title: L("Pairing failed", "配对失败"), digits: "", detail: problem, button: L("Close", "关闭"))
            } else {
                self.panel.show(title: L("Paired", "配对成功"), digits: "", detail: L("The clipboard is now shared with Windows while this VM's window is in front.", "现在当此虚拟机的窗口在前台时，剪贴板会与 Windows 共享。"), button: L("Close", "关闭"))
                DispatchQueue.main.asyncAfter(deadline: .now() + 3) { [weak self] in if self?.client.isPairing == false { self?.panel.hide() } }
            }
        })
    }

    private var folderItems: [NSMenuItem] = []
    private var termination: DispatchSourceSignal?

    /// One menu entry for each mounted shared folder, between the status line and the pairing entry.
    private func showFolders(_ mounts: [SharedFolders.Mounted]) {
        guard let menu = item.menu else { return }
        for old in folderItems { menu.removeItem(old) }
        folderItems = mounts.sorted { $0.name < $1.name }.map { mount in
            let entry = NSMenuItem(title: L("Open “\(mount.name)”", "打开“\(mount.name)”"), action: #selector(openFolder(_:)), keyEquivalent: "")
            entry.target = self
            entry.representedObject = mount.path
            return entry
        }
        if !folderItems.isEmpty { folderItems.append(.separator()) }
        for (offset, entry) in folderItems.enumerated() { menu.insertItem(entry, at: 2 + offset) }
    }

    @objc func openFolder(_ sender: NSMenuItem) {
        if let path = sender.representedObject as? String { NSWorkspace.shared.open(URL(fileURLWithPath: path)) }
    }

    @objc func quit() {
        client.end()
        NSApp.terminate(nil)
    }
}

/// The small window that shows the number to compare while pairing.
final class PairingPanel: NSObject {
    private let window = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 440, height: 260), styleMask: [.titled], backing: .buffered, defer: false)
    private let heading = NSTextField(labelWithString: "")
    private let number = NSTextField(labelWithString: "")
    private let detail = NSTextField(wrappingLabelWithString: "")
    private let button = NSButton(title: "", target: nil, action: nil)
    var onClose: (() -> Void)?

    override init() {
        super.init()
        window.title = "Weims Guest Tools"
        window.isReleasedWhenClosed = false
        window.level = .floating
        heading.font = .boldSystemFont(ofSize: 15)
        number.font = .monospacedDigitSystemFont(ofSize: 60, weight: .semibold)
        number.setAccessibilityLabel(L("Pairing number", "配对数字"))
        detail.alignment = .center
        detail.preferredMaxLayoutWidth = 388
        button.target = self
        button.action = #selector(close)
        button.bezelStyle = .rounded
        let stack = NSStackView(views: [heading, number, detail, button])
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 12
        stack.edgeInsets = NSEdgeInsets(top: 22, left: 26, bottom: 22, right: 26)
        stack.setCustomSpacing(18, after: detail)
        window.contentView = stack
    }

    func show(title: String, digits: String, detail text: String, button label: String) {
        heading.stringValue = title
        number.stringValue = digits
        number.isHidden = digits.isEmpty
        detail.stringValue = text
        button.title = label
        fit()
        if !window.isVisible { window.center() }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    /// Makes the window as tall as what it shows. It shows a large number only while two numbers are
    /// being compared; at its fixed size the other messages left an empty half under the button.
    /// A window that is already on screen keeps its top edge where it is.
    private func fit() {
        guard let content = window.contentView else { return }
        content.layoutSubtreeIfNeeded()
        let size = NSSize(width: 440, height: max(content.fittingSize.height, 120))
        var frame = window.frameRect(forContentRect: NSRect(origin: .zero, size: size))
        frame.origin = NSPoint(x: window.frame.minX, y: window.frame.maxY - frame.height)
        window.setFrame(frame, display: true)
    }

    func hide() { window.orderOut(nil) }

    @objc private func close() {
        hide()
        onClose?()
    }
}

// MARK: - Self-update

/// Replaces the installed app with a newer one that the host sent.
///
/// The connection it came over is authenticated, but that is not what makes the new app trusted:
/// it must carry a valid signature that satisfies this running app's own designated requirement,
/// that is, be signed with the same identity. A build with an ad-hoc signature has a requirement
/// that names one exact build, so such a build never updates itself.
enum Updater {
    static let label = "io.weims.guest-tools"

    private static func numbers(_ version: String) -> [Int]? {
        let parts = version.split(separator: ".", omittingEmptySubsequences: false)
        guard (1...4).contains(parts.count) else { return nil }
        var result: [Int] = []
        for part in parts {
            guard !part.isEmpty, part.count <= 6, part.allSatisfy({ $0.isASCII && $0.isNumber }), let value = Int(part) else { return nil }
            result.append(value)
        }
        return result
    }

    /// Dotted numbers compared number by number. Anything else is never newer.
    static func newer(_ candidate: String, than current: String) -> Bool {
        guard let a = numbers(candidate), let b = numbers(current) else { return false }
        for index in 0..<max(a.count, b.count) {
            let x = index < a.count ? a[index] : 0
            let y = index < b.count ? b[index] : 0
            if x != y { return x > y }
        }
        return false
    }

    /// Unpacks, checks and swaps. Returns nil on success, otherwise what went wrong.
    static func install(archive: Data, version: String) -> String? {
        let files = FileManager.default
        let installed = URL(fileURLWithPath: Bundle.main.bundlePath)
        guard installed.lastPathComponent == "Weims Guest Tools.app" else { return "this copy is not an installed one" }
        let folder = installed.deletingLastPathComponent()
        // Next to the installed app, so that moving the new one into place is a rename on one volume.
        let work = folder.appendingPathComponent(".weims-guest-tools-update-\(UUID().uuidString)")
        defer { try? files.removeItem(at: work) }
        do {
            try files.createDirectory(at: work, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let file = work.appendingPathComponent("helper.tar")
            try archive.write(to: file)
            let tar = Process()
            tar.executableURL = URL(fileURLWithPath: "/usr/bin/tar")
            tar.arguments = ["-xf", file.path, "-C", work.path]
            tar.standardOutput = FileHandle.nullDevice
            tar.standardError = FileHandle.nullDevice
            try tar.run()
            tar.waitUntilExit()
            guard tar.terminationStatus == 0 else { return "the archive could not be unpacked" }
            let fresh = work.appendingPathComponent("Weims Guest Tools.app")
            guard let bundle = Bundle(url: fresh), bundle.bundleIdentifier == Bundle.main.bundleIdentifier,
                  bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String == version else {
                return "the archive does not hold version \(version) of this app"
            }
            if let problem = signedLikeThisCopy(fresh) { return problem }
            let previous = folder.appendingPathComponent(".weims-guest-tools-previous.app")
            try? files.removeItem(at: previous)
            try files.moveItem(at: installed, to: previous)
            do { try files.moveItem(at: fresh, to: installed) } catch {
                try? files.moveItem(at: previous, to: installed)
                throw error
            }
            try? files.removeItem(at: previous)
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    private static func signedLikeThisCopy(_ app: URL) -> String? {
        var running: SecCode?
        var mine: SecStaticCode?
        var requirement: SecRequirement?
        guard SecCodeCopySelf([], &running) == errSecSuccess, let code = running,
              SecCodeCopyStaticCode(code, [], &mine) == errSecSuccess, let own = mine,
              SecCodeCopyDesignatedRequirement(own, [], &requirement) == errSecSuccess, let required = requirement else {
            return "this copy has no signature to compare the new one with"
        }
        var text: CFString?
        SecRequirementCopyString(required, [], &text)
        if let text = text as String?, text.contains("cdhash") {
            return "this copy is not signed with a lasting identity; install the new version from Weims Manager instead"
        }
        var fresh: SecStaticCode?
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &fresh) == errSecSuccess, let candidate = fresh else {
            return "the new app's signature cannot be read"
        }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
        let status = SecStaticCodeCheckValidity(candidate, flags, required)
        return status == errSecSuccess ? nil : "the new app is not signed by the signer of this copy (status \(status))"
    }

    /// Asks launchd to stop this process and start the job again, now from the new files. A copy that
    /// was started by hand is not a launchd job; it keeps running and the new version starts next time.
    static func restart() {
        let tool = Process()
        tool.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        tool.arguments = ["kickstart", "-k", "gui/\(getuid())/\(label)"]
        tool.standardOutput = FileHandle.nullDevice
        tool.standardError = FileHandle.nullDevice
        try? tool.run()
    }
}

// MARK: - Installer

/// The same program is the installer: on the install disk and in the DMG the app is named
/// "Install Weims Guest Tools". Started under that name it copies itself to ~/Applications as
/// "Weims Guest Tools", registers the LaunchAgent, hands over the pairing ticket if the disk carries
/// one as its serial number, and exits at once so that the disk can be ejected. The installed helper then says hello.
enum Installer {
    static let installerName = "Install Weims Guest Tools.app"
    static var isInstaller: Bool { URL(fileURLWithPath: Bundle.main.bundlePath).lastPathComponent == installerName }

    static func run() -> Never {
        let source = Bundle.main.bundlePath
        let volume = URL(fileURLWithPath: source).deletingLastPathComponent().path
        // On the install disk from Weims Manager the pairing ticket is the disk's serial number. No file
        // on the disk is read: that would make macOS ask for access to removable volumes.
        if let serial = Ticket.serialOfInstallDisk(), let ticket = Ticket(serial: serial, volume: volume) {
            if !Store.saveTicket(ticket) { Log.write("installer: the pairing ticket could not be stored") }
        } else {
            Store.removeTicket()
        }
        guard let script = Bundle.main.path(forResource: "install", ofType: "sh") else { fail("install.sh is missing from the app.") }
        let shell = Process()
        shell.executableURL = URL(fileURLWithPath: "/bin/sh")
        shell.arguments = [script, source]
        let output = Pipe()
        shell.standardOutput = output
        shell.standardError = output
        do { try shell.run() } catch { fail("\(error)") }
        let text = String(data: output.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        shell.waitUntilExit()
        if shell.terminationStatus != 0 { fail(text) }
        Log.write("installer: installed from \(source)")
        exit(0)
    }

    private static func fail(_ detail: String) -> Never {
        Store.removeTicket()
        Log.write("installer failed: \(detail)")
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = L("Weims Guest Tools could not be installed", "Weims Guest Tools 安装失败")
        alert.informativeText = detail
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
        exit(1)
    }
}

// MARK: - Entry

let arguments = CommandLine.arguments
if Installer.isInstaller {
    Installer.run()
} else if arguments.count == 2, arguments[1] == "--pair" {
    // Pairing without the menu: for the one-line installer and for scripted setups.
    let client = Client()
    client.pair(onDigits: { digits in
        print(L("Number: \(digits)\nIf Weims Manager on Windows shows the same number, confirm it there.", "数字：\(digits)\n如果 Windows 的 Weims Manager 显示的是同一个数字，请在那边确认。"))
        fflush(stdout)
    }, done: { problem in
        if let problem = problem {
            FileHandle.standardError.write(Data((problem + "\n").utf8))
            exit(1)
        }
        // A helper that is already running picks the new pairing up from the stored file.
        DistributedNotificationCenter.default().postNotificationName(reloadNotification, object: nil, userInfo: nil, deliverImmediately: true)
        print(L("Paired.", "配对成功。"))
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { exit(0) }
    })
    RunLoop.main.run()
} else if arguments.count > 1 {
    print("usage: WeimsGuestTools [--pair]")
    exit(arguments[1] == "--help" ? 0 : 64)
} else {
    if let identifier = Bundle.main.bundleIdentifier,
       NSRunningApplication.runningApplications(withBundleIdentifier: identifier).contains(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier }) {
        exit(0)
    }
    let application = NSApplication.shared
    let delegate = AppDelegate()
    application.delegate = delegate
    application.setActivationPolicy(.accessory)
    application.run()
}
