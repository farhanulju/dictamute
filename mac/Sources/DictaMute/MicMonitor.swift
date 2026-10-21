// Which apps are recording from a microphone right now, via Core Audio's per-process objects
// (macOS 14+). Helper processes (e.g. "Claude Helper") are attributed to the .app they live in.
import AppKit
import CoreAudio
import Darwin

enum MicMonitor {
    /// Names of the apps (as in /Applications, without ".app") with a running input stream.
    static func appsUsingMic() -> Set<String> {
        var names = Set<String>()
        for process in processObjects() where isRunningInput(process) {
            if let pid = pid(of: process), let name = appName(for: pid) { names.insert(name) }
        }
        return names
    }

    private static func processObjects() -> [AudioObjectID] {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyProcessObjectList,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        let system = AudioObjectID(kAudioObjectSystemObject)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &ids) == noErr else { return [] }
        return ids
    }

    private static func isRunningInput(_ process: AudioObjectID) -> Bool {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioProcessPropertyIsRunningInput,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var value: UInt32 = 0
        var size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(process, &address, 0, nil, &size, &value) == noErr && value != 0
    }

    private static func pid(of process: AudioObjectID) -> pid_t? {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioProcessPropertyPID,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var value: pid_t = 0
        var size = UInt32(MemoryLayout<pid_t>.size)
        return AudioObjectGetPropertyData(process, &address, 0, nil, &size, &value) == noErr ? value : nil
    }

    /// "/Applications/Claude.app/Contents/Frameworks/Claude Helper.app/..." -> "Claude".
    static func appName(for pid: pid_t) -> String? {
        var buffer = [CChar](repeating: 0, count: 4096)
        if proc_pidpath(pid, &buffer, UInt32(buffer.count)) > 0 {
            let path = String(cString: buffer)
            if let app = path.split(separator: "/").first(where: { $0.hasSuffix(".app") }) {
                return String(app.dropLast(4))
            }
            return (path as NSString).lastPathComponent
        }
        return NSRunningApplication(processIdentifier: pid)?.localizedName
    }
}
