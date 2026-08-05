# TypeToSquad

An app to forward text to speech into virtual microphones. Inspired by Type to Voice Chat.

A lightweight **Windows system-tray application**: press a hotkey, a small input popup appears near the bottom of the screen, type a message, press Enter — it is spoken aloud through your selected audio output device.

The UI is a native **WPF (.NET 8)** app (the original Godot implementation is kept at [src/TypeToSquad](src/TypeToSquad/)). The TTS engine is a standalone daemon ([WinRTSpeechSynthServer](src/WinRTSpeechSynthServer/)) that wraps the Windows WinRT `SpeechSynthesizer` API, so it uses the TTS voices installed on your system.

## Features

- **System tray** — starts minimized to the tray; left-click summons the input popup, right-click opens a menu with quick settings (voice, volume, output device) and Exit.
- **Global hotkeys** — summon the popup from any app (`Ctrl+Shift+T`) and stop all speech (`Ctrl+Shift+X`). Both are configurable in `config.json`.
- **Input popup** — a borderless, semi-transparent popup near the bottom of the screen (subtitle position). Enter submits, Shift+Enter inserts a newline, Esc hides while keeping your text, Ctrl+↑/↓ navigates past messages.
- **Markup tags** — full support for `[ipa …]`, `[voice hint]`, `[wait t]`, `[sound hint]`, and custom user tags. See [docs/MarkupTags.md](docs/MarkupTags.md).
- **Text replacements** — regex-based macros and pronunciation corrections. See [docs/Replacements.md](docs/Replacements.md).
- **Output device selection** — route speech to any Windows audio endpoint (speakers, headphones, or a virtual cable like **VB-Audio Cable** / **Voicemeeter** for use as a "microphone"). Bluetooth devices are listed with their Windows names.
- **Single instance** — launching the app again summons the popup of the running instance instead of starting a second copy.

> This app does not come with its own virtual microphone. To use it as a "microphone" in voice chat, you need a virtual audio input, like **VB-Audio Cable** or the virtual output of **Voicemeeter**.

## Usage

| Action | Input |
|---|---|
| Summon input popup | `Ctrl+Shift+T` (configurable) |
| Submit / speak | `Enter` |
| Newline | `Shift+Enter` |
| Hide (keep text) | `Esc` |
| Stop all speech | `Ctrl+Shift+X` (configurable) |
| History navigation | `Ctrl+↑` / `Ctrl+↓` |
| Settings | Tray icon → right-click → 设置 (Settings…) |

## Configuration

Settings are saved as JSON in `%AppData%\TypeToSquad\config.json` and can be edited by hand (a restart is required for hotkey changes):

```jsonc
{
  "SummonHotkey": "Ctrl+Shift+T",   // format: Modifier+Modifier+Key (CTRL/ALT/SHIFT/WIN, A-Z/F1-F12/SPACE/...)
  "StopHotkey": "Ctrl+Shift+X",
  "VoiceKey": "Microsoft Huihui (zh-CN)",
  "SynthesisVolumePercent": 100,
  "VoicePitch": 1.0,
  "VoiceRate": 1.0,
  "OutputDevice": "",                // "" = system default; or a device name from the tray menu
  "MaxConcurrentStreams": 6,
  "HistorySlots": 32,
  "MaxReplacementPasses": 20,
  "TextReplacements": [ { "Pattern": "btw", "Replacement": "by the way" } ],
  "VoiceChanges": [ ],
  "SoundEffects": [ ],
  "UserTags": [ ],
  "EnableErrorNotifications": true,
  "EnableWarningNotifications": false
}
```

Logs are written to `%AppData%\TypeToSquad\log.txt`.

## Build

Requires the .NET 8 SDK.

```bash
# Publish a single-file, self-contained exe
dotnet publish src/TypeToSquad.Wpf/TypeToSquad.Wpf.csproj -c Release -o publish/win-x64
```

The output is `publish/win-x64/TypeToSquad.exe` plus the `WinRTSpeechDaemon/` folder — **distribute both together** (the daemon path is resolved relative to the exe). No .NET runtime installation is required on the target machine.

To rebuild the daemon itself, build the [WinRTSpeechSynthServer solution](src/WinRTSpeechSynthServer/); its post-build step copies the binaries into `src/TypeToSquad/WinRTSpeechDaemon/`, which the app bundles.

## Project structure

```
src/
├── TypeToSquad.Core/            # domain logic, no UI dependencies
│   ├── Domain/                  #   AppSettings, RenderNode, HistoryTracker, …
│   ├── Services/                #   MessageProcessor, MessageLexer (markup pipeline)
│   └── Ports/                   #   IAudioPlayer, ISpeechSynthesizer, ISettingsRepository
├── TypeToSquad.Wpf/             # WPF application (tray, popup, settings UI, NAudio, daemon client)
├── TypeToSquad/                 # original Godot implementation (kept for reference)
└── WinRTSpeechSynthServer/      # standalone TTS daemon + shared wire protocol (not Godot)
```

## Docs

- [docs/MarkupTags.md](docs/MarkupTags.md) — markup tag reference (`[ipa]`, `[voice]`, `[wait]`, `[sound]`, custom tags)
- [docs/Replacements.md](docs/Replacements.md) — text replacements (regex rules)
- [docs/Daemon.md](docs/Daemon.md) — the TTS daemon and its protocol
- [docs/domain-model.md](docs/domain-model.md) — domain model of the WPF implementation
- [docs/adr/](docs/adr/) — architecture decision records
