// Menu-bar app: status item, menu, permissions, settings window.
import AppKit
import ServiceManagement
import SwiftUI



final class AppDelegate: NSObject, NSApplicationDelegate {
    private var statusItem: NSStatusItem!
    private let statusLine = NSMenuItem(title: "Starting", action: nil, keyEquivalent: "")
    private let pauseItem = NSMenuItem(title: "Pause", action: #selector(togglePause), keyEquivalent: "")
    private let loginItem = NSMenuItem(title: "Launch at Login", action: #selector(toggleLogin), keyEquivalent: "")
    private let accessibilityItem = NSMenuItem(title: "Allow Accessibility (needed to press Teams' mic button)…", action: #selector(openAccessibility), keyEquivalent: "")
    private let inputItem = NSMenuItem(title: "Allow Input Monitoring (needed for shortcuts)…", action: #selector(openInputMonitoring), keyEquivalent: "")
    private var settings = Settings.load()
    private lazy var engine = Engine(settings: settings)
    private let hotkeys = Hotkeys()
    private var settingsWindow: NSWindow?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let firstRun = !Settings.exists
        if firstRun { settings.save() }

        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        setIcon(.idle)
        statusItem.menu = buildMenu()

        engine.onStatus = { [weak self] text, state in
            self?.statusLine.title = text
            self?.setIcon(state)
        }
        engine.start()

        // Accessibility: to read and press Teams' mic button. Input Monitoring: for shortcuts.
        let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
        _ = AXIsProcessTrustedWithOptions(options)
        if settings.apps.contains(where: { $0.enabled && !Shortcut.parse($0.shortcut).isEmpty }) && !Hotkeys.hasPermission {
            Hotkeys.requestPermission()
        }
        hotkeys.onDown = { [weak self] key, held in self?.engine.keyDown(key, held: held) }
        if !hotkeys.start() {
            Log.write("event tap not available yet (Input Monitoring permission?)")
            // Start listening as soon as the permission is granted, without a relaunch.
            Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] timer in
                guard let self = self, Hotkeys.hasPermission, self.hotkeys.start() else { return }
                Log.write("event tap started")
                timer.invalidate()
            }
        }

        Log.write("started \(appVersion): " + settings.apps.map { "\($0.name) [\($0.enabled ? Shortcut.describe($0.shortcut) : "off")]" }.joined(separator: "; "))
        if firstRun { openSettings() }
    }

    func applicationWillTerminate(_ notification: Notification) { engine.shutdown() }

    private func buildMenu() -> NSMenu {
        let menu = NSMenu()
        menu.delegate = self
        statusLine.isEnabled = false
        menu.addItem(statusLine)
        menu.addItem(.separator())
        menu.addItem(accessibilityItem)
        menu.addItem(inputItem)
        let settingsItem = NSMenuItem(title: "Settings…", action: #selector(openSettings), keyEquivalent: ",")
        menu.addItem(settingsItem)
        menu.addItem(pauseItem)
        menu.addItem(loginItem)
        menu.addItem(NSMenuItem(title: "Open Log", action: #selector(openLog), keyEquivalent: ""))
        menu.addItem(.separator())
        menu.addItem(NSMenuItem(title: "Quit DictaMute", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q"))
        for item in menu.items where item.action != nil && item.action != #selector(NSApplication.terminate(_:)) { item.target = self }
        return menu
    }

    private func setIcon(_ state: TrayState) {
        guard let button = statusItem.button else { return }
        let symbol: String
        switch state {
        case .idle: symbol = "mic"
        case .inCall: symbol = "mic.fill"
        case .muting: symbol = "mic.slash.fill"
        }
        let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "DictaMute")
        image?.isTemplate = true
        button.image = image
        button.contentTintColor = state == .muting ? .systemRed : nil
        button.appearsDisabled = state == .idle
    }

    @objc private func togglePause() {
        pauseItem.state = pauseItem.state == .on ? .off : .on
        engine.setPaused(pauseItem.state == .on)
    }

    @objc private func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            Log.write("Launch at Login failed: \(error.localizedDescription)")
            let alert = NSAlert()
            alert.messageText = "Couldn't change Launch at Login"
            alert.informativeText = "Move DictaMute to the Applications folder and try again.\n\n\(error.localizedDescription)"
            alert.runModal()
        }
    }

    @objc private func openAccessibility() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")!)
    }

    @objc private func openInputMonitoring() {
        Hotkeys.requestPermission()
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ListenEvent")!)
    }

    @objc private func openLog() {
        if FileManager.default.fileExists(atPath: Log.url.path) { NSWorkspace.shared.open(Log.url) }
    }

    @objc func openSettings() {
        if let window = settingsWindow {
            NSApp.activate(ignoringOtherApps: true)
            window.makeKeyAndOrderFront(nil)
            return
        }
        let view = SettingsView(settings: settings) { [weak self] saved in
            guard let self = self else { return }
            self.settings = saved
            saved.save()
            self.engine.update(saved)
            Log.write("settings saved: " + saved.apps.map { "\($0.name) [\($0.enabled ? Shortcut.describe($0.shortcut) : "off")]" }.joined(separator: "; "))
            if saved.apps.contains(where: { $0.enabled && !Shortcut.parse($0.shortcut).isEmpty }) && !Hotkeys.hasPermission {
                Hotkeys.requestPermission()
            }
            self.settingsWindow?.close()
        }
        let window = NSWindow(contentViewController: NSHostingController(rootView: view))
        window.title = "DictaMute Settings"
        window.styleMask = [.titled, .closable]
        window.isReleasedWhenClosed = false
        window.center()
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: window, queue: .main) { [weak self] _ in
            self?.settingsWindow = nil
        }
        settingsWindow = window
        NSApp.activate(ignoringOtherApps: true)
        window.makeKeyAndOrderFront(nil)
    }
}

extension AppDelegate: NSMenuDelegate {
    func menuWillOpen(_ menu: NSMenu) {
        accessibilityItem.isHidden = AXIsProcessTrusted()
        inputItem.isHidden = Hotkeys.hasPermission
        loginItem.state = SMAppService.mainApp.status == .enabled ? .on : .off
    }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory) // menu-bar only, no Dock icon
app.run()
