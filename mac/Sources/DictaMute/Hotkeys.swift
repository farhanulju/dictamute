// Listen-only event tap for keys, modifiers (incl. Fn) and the middle/thumb mouse buttons.
// Needs the Input Monitoring permission. It never blocks or changes input.
import AppKit
import CoreGraphics

final class Hotkeys {
    /// Called on the main thread with the name of a key that just went down and everything held.
    var onDown: ((String, Set<String>) -> Void)?

    private var tap: CFMachPort?
    private var down = Set<String>()
    private var modifiers = Set<String>()

    static var hasPermission: Bool { CGPreflightListenEventAccess() }
    static func requestPermission() { _ = CGRequestListenEventAccess() }

    @discardableResult
    func start() -> Bool {
        let types: [CGEventType] = [.keyDown, .keyUp, .flagsChanged, .otherMouseDown, .otherMouseUp]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        let callback: CGEventTapCallBack = { _, type, event, refcon in
            let me = Unmanaged<Hotkeys>.fromOpaque(refcon!).takeUnretainedValue()
            me.handle(type, event)
            return Unmanaged.passUnretained(event)
        }
        guard let tap = CGEvent.tapCreate(
            tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly,
            eventsOfInterest: mask, callback: callback,
            userInfo: Unmanaged.passUnretained(self).toOpaque()) else { return false }
        self.tap = tap
        let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        return true
    }

    private func handle(_ type: CGEventType, _ event: CGEvent) {
        switch type {
        case .tapDisabledByTimeout, .tapDisabledByUserInput:
            if let tap = tap { CGEvent.tapEnable(tap: tap, enable: true) }
        case .keyDown:
            if event.getIntegerValueField(.keyboardEventAutorepeat) != 0 { return }
            if let name = KeyCodes.byCode[UInt16(event.getIntegerValueField(.keyboardEventKeycode))] { press(name) }
        case .keyUp:
            if let name = KeyCodes.byCode[UInt16(event.getIntegerValueField(.keyboardEventKeycode))] { down.remove(name) }
        case .otherMouseDown, .otherMouseUp:
            let button = event.getIntegerValueField(.mouseEventButtonNumber) // 2 middle, 3 back, 4 forward
            guard (2...4).contains(button) else { return }
            let name = "Mouse\(button + 1)"
            if type == .otherMouseDown { press(name) } else { down.remove(name) }
        case .flagsChanged:
            let flags = event.flags
            var now = Set<String>()
            if flags.contains(.maskControl) { now.insert("Ctrl") }
            if flags.contains(.maskShift) { now.insert("Shift") }
            if flags.contains(.maskAlternate) { now.insert("Option") }
            if flags.contains(.maskCommand) { now.insert("Cmd") }
            if flags.contains(.maskSecondaryFn) { now.insert("Fn") }
            for released in modifiers.subtracting(now) { down.remove(released) }
            let pressed = now.subtracting(modifiers)
            modifiers = now
            for name in pressed { press(name) }
        default:
            break
        }
    }

    private func press(_ name: String) {
        guard !down.contains(name) else { return }
        down.insert(name)
        onDown?(name, down)
    }
}
