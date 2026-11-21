// SPDX-License-Identifier: GPL-2.0-or-later
// Weims Guest Tools: the macOS side of clipboard sharing with the Windows host.
//
// It runs as the logged-in user, needs no root and installs nothing into the system. It connects to
// the host's broker at 10.0.2.2 (QEMU's user-mode network maps that to the host's loopback), checks
// the broker's certificate against the fingerprint remembered at pairing, and shows its token.
import AppKit
import CryptoKit
import Foundation
import Network

let helperVersion = "0.1.0"
let protocolVersion = 1
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
        do {
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
            let temporary = file.path + ".tmp"
            // Created with its final permissions, then moved into place, so the token is never readable by others.
            try? FileManager.default.removeItem(atPath: temporary)
            guard FileManager.default.createFile(atPath: temporary, contents: try JSONEncoder().encode(pairing), attributes: [.posixPermissions: 0o600]) else { return false }
            return rename(temporary, file.path) == 0
        } catch { return false }
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
        // No link, no session: the keep-alive check must not judge the next connection by the last one.
        phase = .hello
        active = false
        cancelProbes()
    }

    private func cancelProbes() {
        let pending = probes
        probes = []
        for probe in pending { probe.onClosed = nil; probe.close("no longer needed") }
    }

    // MARK: Connecting

    private func connect() {
        guard !suspended, link == nil, probes.isEmpty, pairingDone == nil else { return }
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
        link.send(["type": "hello", "version": protocolVersion, "helper": helperVersion, "capabilities": ["clipboard-text"]])
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
        if wasSession { retryDelay = 2 }
        scheduleRetry()
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
                attempt?.link.send(["type": "hello", "version": protocolVersion, "helper": helperVersion, "capabilities": ["clipboard-text"]])
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
    }
}

// MARK: - Menu bar

final class AppDelegate: NSObject, NSApplicationDelegate {
    let client = Client()
    var item: NSStatusItem!
    let statusLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")

    func applicationDidFinishLaunching(_ notification: Notification) {
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        item.button?.image = NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "Weims Guest Tools")
        if item.button?.image == nil { item.button?.title = "W" }
        let menu = NSMenu()
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())
        menu.addItem(withTitle: L("Pair with Host…", "与宿主配对…"), action: #selector(pair), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: L("Quit Weims Guest Tools", "退出 Weims Guest Tools"), action: #selector(quit), keyEquivalent: "q").target = self
        item.menu = menu
        client.onStatus = { [weak self] text in self?.statusLine.title = text }
        client.start()
        statusLine.title = client.status
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

    @objc func quit() { NSApp.terminate(nil) }
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
        window.contentView = stack
    }

    func show(title: String, digits: String, detail text: String, button label: String) {
        heading.stringValue = title
        number.stringValue = digits
        number.isHidden = digits.isEmpty
        detail.stringValue = text
        button.title = label
        if !window.isVisible { window.center() }
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }

    func hide() { window.orderOut(nil) }

    @objc private func close() {
        hide()
        onClose?()
    }
}

// MARK: - Entry

let arguments = CommandLine.arguments
if arguments.count == 2, arguments[1] == "--pair" {
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
