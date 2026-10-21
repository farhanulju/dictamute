// Settings: which dictation apps DictaMute watches and how each one "starts".
// Same JSON shape as the Windows version, stored in ~/Library/Application Support/DictaMute.
import Foundation

let version = "1.3.0-beta.1"

struct AppRule: Codable, Identifiable, Equatable {
    var id = UUID()
    var name: String
    var process: String   // the app's name as in /Applications, without ".app" (e.g. "Wispr Flow")
    var enabled: Bool
    var shortcut: String  // "" = mute whenever it uses the mic; else combos like "Fn, Ctrl+Option+Space"

    enum CodingKeys: String, CodingKey {
        case name = "Name", process = "Process", enabled = "Enabled", shortcut = "Shortcut"
    }

    func matches(_ appName: String) -> Bool {
        appName.caseInsensitiveCompare(process) == .orderedSame
            || appName.lowercased().hasPrefix(process.lowercased() + " ")
    }
}

struct Settings: Codable, Equatable {
    var apps: [AppRule]
    var unmuteDelayMs: Int

    enum CodingKeys: String, CodingKey { case apps = "Apps", unmuteDelayMs = "UnmuteDelayMs" }

    static var directory: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/DictaMute", isDirectory: true)
    }
    static var fileURL: URL { directory.appendingPathComponent("settings.json") }
    static var exists: Bool { FileManager.default.fileExists(atPath: fileURL.path) }

    static var defaults: Settings {
        Settings(apps: [
            // Wispr Flow's default push-to-talk on the Mac is the Fn (globe) key.
            AppRule(name: "Wispr Flow", process: "Wispr Flow", enabled: true, shortcut: "Fn"),
            AppRule(name: "Claude", process: "Claude", enabled: true, shortcut: ""),
        ], unmuteDelayMs: 600)
    }

    static func load() -> Settings {
        guard let data = try? Data(contentsOf: fileURL),
              var s = try? JSONDecoder().decode(Settings.self, from: data) else { return defaults }
        if !(100...10000).contains(s.unmuteDelayMs) { s.unmuteDelayMs = 600 }
        return s
    }

    func save() {
        try? FileManager.default.createDirectory(at: Settings.directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted]
        if let data = try? encoder.encode(self) { try? data.write(to: Settings.fileURL) }
    }
}

/// Shortcut combos as sets of key names: modifiers (Ctrl, Shift, Option, Cmd, Fn), middle/thumb
/// mouse buttons (Mouse3, Mouse4, Mouse5) and ordinary keys (A, F13, Space, ...).
enum Shortcut {
    static let modifierOrder = ["Ctrl", "Shift", "Option", "Cmd", "Fn"]

    /// "Fn, Ctrl+Option+Space" -> two combos. Unknown names are skipped.
    static func parse(_ text: String) -> [Set<String>] {
        text.split(separator: ",").compactMap { part in
            let combo = Set(part.split(separator: "+").compactMap { canonical(String($0)) })
            return combo.isEmpty ? nil : combo
        }
    }

    static func format(_ combo: Set<String>) -> String {
        combo.sorted { rank($0) == rank($1) ? $0 < $1 : rank($0) < rank($1) }.joined(separator: "+")
    }

    static func describe(_ text: String) -> String {
        let combos = parse(text)
        if combos.isEmpty { return "Whenever it uses the mic" }
        return "After " + combos.map(format).joined(separator: " or ")
    }

    private static func rank(_ key: String) -> Int {
        if let i = modifierOrder.firstIndex(of: key) { return i }
        return key.hasPrefix("Mouse") ? 10 : 5
    }

    static func canonical(_ raw: String) -> String? {
        let name = raw.trimmingCharacters(in: .whitespaces)
        switch name.lowercased() {
        case "ctrl", "control": return "Ctrl"
        case "shift": return "Shift"
        case "alt", "option", "opt": return "Option"
        case "cmd", "command", "win": return "Cmd"
        case "fn", "globe", "function": return "Fn"
        case "mouse3", "mouse4", "mouse5": return "Mouse" + String(name.last!)
        default: break
        }
        if let key = KeyCodes.byName.keys.first(where: { $0.caseInsensitiveCompare(name) == .orderedSame }) { return key }
        return nil
    }
}

/// macOS virtual key codes (ANSI layout) for the keys a shortcut can use.
enum KeyCodes {
    static let byCode: [UInt16: String] = {
        var map: [UInt16: String] = [
            0: "A", 1: "S", 2: "D", 3: "F", 4: "H", 5: "G", 6: "Z", 7: "X", 8: "C", 9: "V",
            11: "B", 12: "Q", 13: "W", 14: "E", 15: "R", 16: "Y", 17: "T", 31: "O", 32: "U",
            34: "I", 35: "P", 37: "L", 38: "J", 40: "K", 45: "N", 46: "M",
            18: "1", 19: "2", 20: "3", 21: "4", 23: "5", 22: "6", 26: "7", 28: "8", 25: "9", 29: "0",
            36: "Return", 48: "Tab", 49: "Space", 51: "Delete", 53: "Escape", 50: "Grave",
            122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8",
            101: "F9", 109: "F10", 103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15",
            106: "F16", 64: "F17", 79: "F18", 80: "F19", 90: "F20",
            123: "Left", 124: "Right", 125: "Down", 126: "Up",
            115: "Home", 119: "End", 116: "PageUp", 121: "PageDown", 117: "ForwardDelete",
        ]
        map[114] = "Help"
        return map
    }()
    static let byName: [String: UInt16] = Dictionary(uniqueKeysWithValues: byCode.map { ($1, $0) })
}
