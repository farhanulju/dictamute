// Settings window (SwiftUI): the watched apps, how each starts, and the unmute delay.
import SwiftUI

struct SettingsView: View {
    @State var settings: Settings
    let onSave: (Settings) -> Void
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Mute Teams while these apps are listening").font(.headline)
            Text("During a Teams call, DictaMute mutes your Teams mic while one of these apps is taking dictation, then unmutes it when the app stops. Your microphone itself is never muted.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            ForEach($settings.apps) { $app in
                AppRow(app: $app) { settings.apps.removeAll { $0.id == app.id } }
            }
            Button {
                settings.apps.append(AppRule(name: "", process: "", enabled: true, shortcut: ""))
            } label: { Label("Add app", systemImage: "plus") }

            Divider()
            Stepper(value: $settings.unmuteDelayMs, in: 100...10000, step: 100) {
                Text("Unmute Teams \(settings.unmuteDelayMs) ms after the app stops listening")
            }
            if let problem = problem {
                Text(problem).foregroundStyle(.red).fixedSize(horizontal: false, vertical: true)
            }
            HStack {
                Text("DictaMute \(version) · experimental macOS build").foregroundStyle(.secondary).font(.caption)
                Spacer()
                Button("Save") { save() }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .frame(width: 600)
    }

    private func save() {
        if settings.apps.contains(where: { $0.process.trimmingCharacters(in: .whitespaces).isEmpty }) {
            problem = "Every app needs its name as shown in the Applications folder (e.g. \"Wispr Flow\")."
            return
        }
        var saved = settings
        for i in saved.apps.indices {
            saved.apps[i].process = saved.apps[i].process.trimmingCharacters(in: .whitespaces)
            if saved.apps[i].process.hasSuffix(".app") { saved.apps[i].process.removeLast(4) }
            if saved.apps[i].name.trimmingCharacters(in: .whitespaces).isEmpty { saved.apps[i].name = saved.apps[i].process }
        }
        onSave(saved)
    }
}

private struct AppRow: View {
    @Binding var app: AppRule
    let onDelete: () -> Void
    @State private var usesShortcut: Bool

    init(app: Binding<AppRule>, onDelete: @escaping () -> Void) {
        _app = app
        self.onDelete = onDelete
        _usesShortcut = State(initialValue: !app.wrappedValue.shortcut.isEmpty)
    }

    var body: some View {
        GroupBox {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Toggle("", isOn: $app.enabled).labelsHidden()
                    TextField("App (as in Applications, e.g. Claude)", text: $app.process)
                    TextField("Display name", text: $app.name).frame(width: 150)
                    Button(role: .destructive, action: onDelete) { Image(systemName: "trash") }
                        .buttonStyle(.borderless)
                        .help("Remove this app")
                }
                Picker("Mute Teams", selection: $usesShortcut) {
                    Text("Whenever it uses the mic").tag(false)
                    Text("After a shortcut").tag(true)
                }
                .pickerStyle(.segmented)
                .onChange(of: usesShortcut) { _, newValue in if !newValue { app.shortcut = "" } }
                if usesShortcut {
                    TextField("Same shortcut as in the app, e.g. Fn, Ctrl+Option+Space, Mouse5", text: $app.shortcut)
                    Text(Shortcut.parse(app.shortcut).isEmpty
                         ? "Type key names joined with +. Modifiers: Ctrl, Shift, Option, Cmd, Fn. Mouse: Mouse3 (middle), Mouse4, Mouse5."
                         : Shortcut.describe(app.shortcut))
                        .font(.caption).foregroundStyle(.secondary)
                } else {
                    Text("Best for apps that only open the mic while you talk to them, like Claude.")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            .padding(4)
        }
    }
}
