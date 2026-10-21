// Teams' in-call mic button, found and pressed through the macOS Accessibility API.
// UNVERIFIED on a real Mac: the button is matched by its web id ("microphone-button", the id the
// Windows version uses) or by its "Mute mic" / "Unmute mic" label. The log records what was found.
import AppKit
import ApplicationServices

final class TeamsMic {
    private var button: AXUIElement?
    private let log: (String) -> Void

    init(log: @escaping (String) -> Void) { self.log = log }

    var hasButton: Bool { button != nil }
    func forget() { button = nil }

    static var teamsApp: NSRunningApplication? {
        NSWorkspace.shared.runningApplications.first {
            ($0.bundleIdentifier ?? "").hasPrefix("com.microsoft.teams") && $0.activationPolicy == .regular
        }
    }

    func find() {
        button = nil
        guard let app = TeamsMic.teamsApp else { return }
        let axApp = AXUIElementCreateApplication(app.processIdentifier)
        AXUIElementSetMessagingTimeout(axApp, 1.0)
        // Chromium-based apps only build their web accessibility tree once a client asks for it.
        AXUIElementSetAttributeValue(axApp, "AXManualAccessibility" as CFString, kCFBooleanTrue)
        if search(axApp) { return }
        // Some Chromium builds only respond to the VoiceOver-style flag. It can make window
        // managers animate oddly, so it's the fallback rather than the default.
        if !triedEnhancedUI {
            triedEnhancedUI = true
            AXUIElementSetAttributeValue(axApp, "AXEnhancedUserInterface" as CFString, kCFBooleanTrue)
            log("retrying with AXEnhancedUserInterface")
            _ = search(axApp)
        }
    }

    private var triedEnhancedUI = false

    private func search(_ axApp: AXUIElement) -> Bool {
        let started = Date()
        var queue: [(AXUIElement, Int)] = (TeamsMic.children(axApp, kAXWindowsAttribute) ?? []).map { ($0, 0) }
        var next = 0
        while next < queue.count, next < 30000, Date().timeIntervalSince(started) < 2.0 {
            let (element, depth) = queue[next]
            next += 1
            if TeamsMic.isMicButton(element) {
                button = element
                log("found Teams mic button: " + TeamsMic.describe(element))
                return true
            }
            if depth < 80, let kids = TeamsMic.children(element, kAXChildrenAttribute) {
                queue.append(contentsOf: kids.map { ($0, depth + 1) })
            }
        }
        log("Teams mic button not found (searched \(next) elements" + (AXIsProcessTrusted() ? ")" : "; Accessibility permission missing)"))
        return false
    }

    /// true = muted, false = live, nil = no call / button not found.
    func isMuted() -> Bool? {
        for attempt in 0..<2 {
            guard let button = button else { if attempt == 0 { find() }; continue }
            guard let label = TeamsMic.label(button) else { find(); continue }
            if label.lowercased().hasPrefix("unmute") { return true }
            if label.lowercased().hasPrefix("mute") { return false }
            log("unrecognised mic button label: '\(label)'")
            return nil
        }
        return nil
    }

    func press() -> Bool {
        guard let button = button else { return false }
        let result = AXUIElementPerformAction(button, kAXPressAction as CFString)
        if result != .success { log("pressing the Teams mic button failed: AXError \(result.rawValue)") }
        return result == .success
    }

    // ---- AX helpers -------------------------------------------------------------------------

    private static func isMicButton(_ e: AXUIElement) -> Bool {
        guard string(e, kAXRoleAttribute) == (kAXButtonRole as String) else { return false }
        if string(e, "AXDOMIdentifier") == "microphone-button" || string(e, kAXIdentifierAttribute) == "microphone-button" { return true }
        guard let label = label(e)?.lowercased() else { return false }
        return label.hasPrefix("mute mic") || label.hasPrefix("unmute mic")
    }

    /// The button's visible label: title, then description, then help.
    private static func label(_ e: AXUIElement) -> String? {
        for attr in [kAXTitleAttribute, kAXDescriptionAttribute, kAXHelpAttribute] {
            if let s = string(e, attr), !s.isEmpty { return s }
        }
        return nil
    }

    private static func describe(_ e: AXUIElement) -> String {
        "role=\(string(e, kAXRoleAttribute) ?? "?") dom=\(string(e, "AXDOMIdentifier") ?? "-") " +
        "id=\(string(e, kAXIdentifierAttribute) ?? "-") label='\(label(e) ?? "")'"
    }

    private static func string(_ e: AXUIElement, _ attr: String) -> String? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(e, attr as CFString, &value) == .success else { return nil }
        return value as? String
    }

    private static func children(_ e: AXUIElement, _ attr: String) -> [AXUIElement]? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(e, attr as CFString, &value) == .success else { return nil }
        return value as? [AXUIElement]
    }
}
