// The per-app state machine, a port of the Windows version (src/DictaMute.cs). All state lives on
// one serial queue polled ~10 times a second; shortcuts arrive from the main thread.
import AppKit

enum TrayState { case idle, inCall, muting }

final class Engine {
    private enum State { case idle, arming, listening }

    private final class Watch {
        let rule: AppRule
        let combos: [Set<String>]
        var state = State.idle
        var active = false, wasActive = false
        var armDeadline = Date.distantPast, lastActive = Date.distantPast
        init(rule: AppRule) { self.rule = rule; combos = Shortcut.parse(rule.shortcut) }
    }

    private static let armTimeout: TimeInterval = 2.5 // shortcut -> app must start listening

    var onStatus: ((String, TrayState) -> Void)?   // called on the main thread
    private let queue = DispatchQueue(label: "DictaMute.engine")
    private var timer: DispatchSourceTimer?
    private var settings: Settings
    private var watches: [Watch]
    private var paused = false
    private lazy var teams = TeamsMic(log: Log.write)
    private var lastTeamsScan = Date.distantPast
    private var ownMute = false       // we pressed Teams' button to mute and owe an unmute
    private var muteConfirmed = false // Teams' button has since shown "Unmute" (its label lags the press)
    private var pressedAt = Date.distantPast
    private var lastStatus = ""

    init(settings: Settings) {
        self.settings = settings
        watches = Engine.build(settings)
    }

    func start() {
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now(), repeating: .milliseconds(100))
        t.setEventHandler { [weak self] in self?.tick() }
        t.resume()
        timer = t
    }

    func update(_ settings: Settings) {
        queue.async { self.settings = settings; self.watches = Engine.build(settings) }
    }

    func setPaused(_ value: Bool) {
        queue.async { self.paused = value; Log.write(value ? "paused" : "resumed") }
    }

    /// From the hotkey tap: a key went down; arm every app whose shortcut it completes.
    func keyDown(_ key: String, held: Set<String>) {
        queue.async {
            for w in self.watches where w.combos.contains(where: { $0.contains(key) && $0.isSubset(of: held) }) {
                self.arm(w)
            }
        }
    }

    /// Undo our mute before quitting.
    func shutdown() {
        queue.sync {
            timer?.cancel()
            if ownMute, teams.isMuted() == true { _ = teams.press() }
        }
    }

    private static func build(_ s: Settings) -> [Watch] {
        s.apps.filter { $0.enabled && !$0.process.isEmpty }.map(Watch.init)
    }

    // ---- queue only -------------------------------------------------------------------------

    private var inCall = false

    // A shortcut only arms the app. Teams is muted once the app is actually listening, so
    // nothing touches Teams while the app is still starting its dictation.
    private func arm(_ w: Watch) {
        guard w.state == .idle, !paused, inCall else { return } // a stop press is seen via the mic going idle
        if w.active { Log.write("\(w.rule.name): shortcut, but it's already using the mic; leaving Teams alone"); return }
        Log.write("\(w.rule.name): shortcut, armed")
        w.state = .arming
        w.armDeadline = Date().addingTimeInterval(Engine.armTimeout)
    }

    private func tick() {
        let using = MicMonitor.appsUsingMic()
        inCall = using.contains { $0.hasPrefix("Microsoft Teams") } // Teams holds the mic only in calls
        for w in watches { w.active = using.contains(where: w.rule.matches) }

        if !inCall { teams.forget() }
        else if !teams.hasButton, Date().timeIntervalSince(lastTeamsScan) > 3 {
            lastTeamsScan = Date()
            teams.find()
        }

        let now = Date()
        let delay = TimeInterval(settings.unmuteDelayMs) / 1000
        let wasListening = watches.contains { $0.state == .listening }
        for w in watches {
            if w.active { w.lastActive = now }
            if !inCall || paused { w.state = .idle }
            else {
                switch w.state {
                case .idle:
                    // "Whenever it uses the mic" apps start on the idle -> active edge.
                    if w.combos.isEmpty, w.active, !w.wasActive { w.state = .listening; Log.write("\(w.rule.name): started listening") }
                case .arming:
                    if w.active { w.state = .listening; Log.write("\(w.rule.name): started listening") }
                    else if now > w.armDeadline { w.state = .idle; Log.write("\(w.rule.name): didn't start listening") }
                case .listening:
                    if !w.active, now.timeIntervalSince(w.lastActive) > delay { w.state = .idle; Log.write("\(w.rule.name): stopped listening") }
                }
            }
            w.wasActive = w.active
        }

        let listening = watches.first { $0.state == .listening }
        if listening != nil, !wasListening {
            if teams.isMuted() == false { pressTeams("muted Teams", muting: true) } else { Log.write("Teams already muted") }
        } else if listening == nil, wasListening {
            restore()
        } else if !inCall {
            ownMute = false
        }

        if ownMute {
            let muted = teams.isMuted()
            if !muteConfirmed {
                if muted == true { muteConfirmed = true }
                else if now.timeIntervalSince(pressedAt) > 3 { ownMute = false; Log.write("Teams never showed muted; not tracking") }
            } else if muted == false {
                // You unmuted yourself mid-dictation: don't fight it, and don't "restore" later.
                ownMute = false
                Log.write("you unmuted manually")
            }
        }

        let status = paused ? "Paused"
            : !inCall ? "Not in a call"
            : ownMute && listening != nil ? "Muted Teams for \(listening!.rule.name)"
            : "In a call"
        let tray: TrayState = paused || !inCall ? .idle : ownMute ? .muting : .inCall
        if status != lastStatus {
            lastStatus = status
            DispatchQueue.main.async { self.onStatus?(status, tray) }
        }
    }

    private func restore() {
        guard ownMute else { return }
        // Our mute may not show yet (very short dictation): give Teams a moment to catch up.
        let until = Date().addingTimeInterval(2)
        while !muteConfirmed, Date() < until {
            if teams.isMuted() == true { muteConfirmed = true } else { Thread.sleep(forTimeInterval: 0.05) }
        }
        if teams.isMuted() == true { pressTeams("unmuted Teams", muting: false) } else { Log.write("Teams already live, nothing to undo") }
        ownMute = false
    }

    private func pressTeams(_ what: String, muting: Bool) {
        let before = NSWorkspace.shared.frontmostApplication
        guard teams.press() else { return }
        if muting { ownMute = true; muteConfirmed = false; pressedAt = Date() }
        Log.write(what)
        // Pressing a Teams button must not pull focus away from where you're dictating.
        for _ in 0..<6 {
            Thread.sleep(forTimeInterval: 0.04)
            let now = NSWorkspace.shared.frontmostApplication
            if let before = before, now?.processIdentifier != before.processIdentifier {
                Log.write("focus moved to \(now?.localizedName ?? "?"); restoring \(before.localizedName ?? "?")")
                _ = before.activate()
                break
            }
        }
    }
}

enum Log {
    static let url = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/DictaMute.log")
    private static let lock = NSLock()
    private static let stamp: DateFormatter = {
        let f = DateFormatter()
        f.dateFormat = "yyyy-MM-dd HH:mm:ss.SSS"
        return f
    }()

    static func write(_ line: String) {
        lock.lock(); defer { lock.unlock() }
        let fm = FileManager.default
        if let size = (try? fm.attributesOfItem(atPath: url.path))?[.size] as? Int, size > 512 * 1024 {
            try? fm.removeItem(at: url)
        }
        let data = Data((stamp.string(from: Date()) + " " + line + "\n").utf8)
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile(); handle.write(data); try? handle.close()
        } else {
            try? data.write(to: url)
        }
    }
}
