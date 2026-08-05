# TypeToSquad — Ubiquitous Language

A Windows system-tray application that lets users type messages and have them spoken aloud via TTS.

## Core Workflow

```
User summons input popup (hotkey)
  → types a Message (raw text, may contain Markup Tags)
  → submits (Enter)
  → message is Processed through the markup pipeline
  → result is Synthesized by the daemon into audio
  → audio is Played through the output device
```

## Glossary

- **Message** — The raw text a user types into the input popup. May contain zero or more Markup Tags interspersed with plain text.

- **Markup Tag** — A `[type argument]` annotation embedded in a Message. Built-in types:
  - `[ipa phonemes]` — pronounce using IPA phonemes
  - `[voice hint]` — switch to a different TTS voice mid-message
  - `[sound hint]` / `[audio hint]` — insert a Sound Effect
  - `[wait duration]` / `[break duration]` — insert a pause
  - `[]` (empty) — reset running voice changes
  Custom User Tags are also supported (see below).

- **Text Replacement** — A regex pattern → substitution rule applied to the Message before tag processing. Used for macros (e.g., expanding "btw" → "by the way") and pronunciation corrections. Multiple replacements run in sequence across multiple passes.

- **User Tag** — A custom Markup Tag type defined in settings. Each has a tag type name, a regex pattern, and a replacement string. When a tag of that type is encountered, the pattern→replacement is applied to its argument. User Tags are also a form of Text Replacement, scoped to tag arguments.

- **Render Node** — A node in the processed-message tree. Types: `Text`, `SsmlRoot` (SSML `<speak>` wrapper), `Voice`, `Phoneme` (IPA), `Break` (pause), `Sound`, `Serial` (ordered sequence for playback). The Render Node tree is the output of Message processing and the input to synthesis.

- **SSML** — Speech Synthesis Markup Language, the standard XML format consumed by TTS engines. The app generates SSML from the Render Node tree before sending it to the daemon.

- **Voice** — A TTS voice installed on the system. Each Voice has a Name and a Language (e.g., "Microsoft Zira (en-US)"). Voices are discovered by querying the Daemon at startup.

- **Voice Change** — A mapping from a hint string to a Voice, stored in settings. Used by the `[voice hint]` tag to switch which Voice speaks subsequent text in the message.

- **Daemon** — The external Windows TTS process (`WinRTSpeechSynthServer.exe`). It wraps the WinRT `SpeechSynthesizer` API and communicates with the app via a Windows named pipe. One daemon process runs per app instance. The daemon accepts synthesis requests (plain text or SSML, with voice/pitch/rate/volume parameters) and returns PCM WAV audio bytes.

- **Sound Effect** — A pre-recorded audio file (.wav, .ogg, or .mp3) mapped to a hint string in settings. Inserted into the playback stream by `[sound hint]` tags.

- **Playback** — The act of playing synthesized audio through the selected Output Device. Multiple Playbacks can run concurrently (up to the configured max). Starting a new Playback when at the max stops the oldest one.

- **Output Device** — A Windows audio endpoint (speakers, virtual cable, etc.) selected by the user for audio output.

- **History Entry** — A previously submitted Message, stored in memory for quick recall. Navigated with Ctrl+↑ / Ctrl+↓ within the input popup.

- **Hotkey** — A global keyboard shortcut registered with Windows. Two hotkeys:
  - **Summon Hotkey** (default Ctrl+Shift+T) — shows the input popup, regardless of which app has focus.
  - **Stop Hotkey** (default Ctrl+Shift+X) — immediately stops all active Playback.

- **Input Popup** — The borderless, semi-transparent window that appears when the Summon Hotkey is pressed. Contains the Message text area. Submitting or pressing Esc hides it. Positioned near the bottom-center of the screen.

- **System Tray Icon** — The app's icon in the Windows notification area. Left-click summons the Input Popup. Right-click shows a context menu with quick settings and app exit.
