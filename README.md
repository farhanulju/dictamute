# DictaMute

Mute **only Microsoft Teams** while you dictate with [Wispr Flow](https://wisprflow.ai), so
people in your call don't hear your dictation. Your system microphone is never touched, so
Wispr keeps recording normally.

A tiny Windows tray app: one ~20 KB exe, no installer, no dependencies.

## How it works

1. You press **mouse button 5** (the forward thumb button) to start Wispr dictation. The
   click is passed straight through to Wispr — DictaMute only listens.
2. As soon as Wispr's microphone stream becomes active, DictaMute presses Teams' own
   **mic button** in your meeting (via Windows UI Automation — no fake keystrokes).
3. When Wispr stops recording, it presses the button again to unmute you.

It leaves Teams alone when:

- you're not in a Teams call,
- you're already muted in Teams,
- you unmute yourself manually mid-dictation,
- Wispr doesn't actually start recording after the click.

Works with Wispr's push-to-talk set to **press-and-hold** or **click (toggle)**.

## Requirements

- Windows 10/11, 64-bit (uses the built-in .NET Framework 4.x compiler)
- The **new** Microsoft Teams, in **English** (it reads the "Mute mic" / "Unmute mic" button label)
- Wispr Flow with its push-to-talk shortcut on **mouse button 5**

## Download and run

1. Download **`DictaMute.exe`** from the [latest release](https://github.com/farhanulju/dictamute/releases/latest).
2. Put it somewhere permanent (e.g. `Documents\DictaMute\`) and double-click it.
3. Windows SmartScreen may say *"Windows protected your PC"* because the exe isn't code-signed.
   Click **More info → Run anyway**. (Prefer not to? Build it yourself — see below.)
4. A shield icon appears in the system tray. Right-click it and tick **Start with Windows**
   so it runs every time you sign in.

The tray menu also has the current status (`Not in a call` / `In a call` / `Muted Teams for
dictation`), **Open log**, and **Exit**.

To remove: untick **Start with Windows**, choose **Exit**, and delete the exe (plus
`%LOCALAPPDATA%\DictaMute`, which holds the log).

## Build from source

No SDK needed — it uses the C# compiler that ships with Windows.

```bat
git clone https://github.com/farhanulju/dictamute.git
cd dictamute
install.cmd
```

`install.cmd` builds the app, copies it to `%LOCALAPPDATA%\DictaMute`, turns on Start with
Windows, and starts it. `uninstall.cmd` undoes all of that. `build.cmd` only builds (into `bin\`).

## Known limitations

- **The very first moment of dictation can still reach the call.** Teams is muted once Wispr
  has started recording (typically 0.1–0.5 s after the click), not before — muting earlier
  interferes with Wispr's startup.
- **Focus briefly flickers to Teams.** Pressing Teams' button moves focus to the meeting
  window; DictaMute restores your previous window within ~80 ms.
- Relies on Teams' internal button id (`microphone-button`) and English labels; a Teams
  update or another UI language may need a code change.
- Only mouse button 5 arms it today. If your Wispr shortcut is on the keyboard, see
  `OnPress` / `HookCallback` in `src/DictaMute.cs`.

## Troubleshooting

- Tray icon → **Open log** shows every step (`click: armed`, `Wispr recording -> muted Teams`,
  `unmuted Teams`, focus restores).
- `CaptureProbe.exe 30` (built with the source, or in `%LOCALAPPDATA%\DictaMute` after
  `install.cmd`) prints which processes have an active microphone stream for 30 seconds —
  useful to check that Wispr and Teams are detected.

## Files

| File | What it is |
|------|-----------|
| `src/DictaMute.cs` | Tray app: mouse hook, state machine, Teams UI Automation, focus restore, autostart |
| `src/AudioCapture.cs` | Core Audio interop: which processes have an active mic stream |
| `src/CaptureProbe.cs` | Small console diagnostic built on `AudioCapture.cs` |
| `build.cmd` / `install.cmd` / `uninstall.cmd` | Build, install + autostart, remove |

## License

MIT
