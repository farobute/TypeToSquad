# TypeToSquad — Domain Model

## Architecture Overview

The application is split into two layers: a **Core** class library (pure domain logic, no UI or I/O) and a **Wpf** application (UI + infrastructure adapters).

```
┌─────────────────────────────────────────────┐
│  TypeToSquad.Wpf                            │
│  ┌──────────┐ ┌──────────┐ ┌─────────────┐  │
│  │ UI Layer │ │ Services │ │Infrastructure│  │
│  │ (Windows)│ │(Hotkey,  │ │(NAudio,      │  │
│  │          │ │ Playback,│ │ RegisterHot  │  │
│  │          │ │ Settings)│ │ Key, etc.)   │  │
│  └──────────┘ └────┬─────┘ └──────┬──────┘  │
│                    │              │          │
├────────────────────┼──────────────┼──────────┤
│  TypeToSquad.Core  │              │          │
│  ┌─────────────────▼──────────────▼───────┐  │
│  │          Domain Services               │  │
│  │  MessageProcessor, MessageLexer        │  │
│  ├────────────────────────────────────────┤  │
│  │          Domain Model                  │  │
│  │  AppSettings, Message, RenderNode      │  │
│  │  Value Objects, Repository interfaces  │  │
│  ├────────────────────────────────────────┤  │
│  │          Protocol (shared)             │  │
│  │  WinRTSpeechSynthServer.Protocol.dll   │  │
│  └────────────────────────────────────────┘  │
└─────────────────────────────────────────────┘
```

## Aggregate Roots

### 1. AppSettings

The single configuration aggregate. All user-configurable state lives under this root.

**Invariants:**
- `SynthesisVolumePercent` ∈ [0, 100]
- `VoicePitch` ∈ [0.0, 2.0]
- `VoiceRate` ∈ [0.5, 6.0]
- `MaxConcurrentStreams` ∈ [1, 64]
- `HistorySlots` ≥ 0
- `MaxReplacementPasses` ∈ [0, 100]
- `VoiceKey` must be one of the discovered voices (runtime invariant)
- `Device` must be one of the available output devices (runtime invariant)

**Contents (Value Objects):**

| Field | Type | Description |
|---|---|---|
| SummonHotkey | HotkeyBinding | Global shortcut to show input popup |
| StopHotkey | HotkeyBinding | Global shortcut to stop playback |
| VoiceKey | string | Selected TTS voice key |
| SynthesisVolumePercent | int | 0-100 |
| VoicePitch | double | 0.0-2.0, default 1.0 |
| VoiceRate | double | 0.5-6.0, default 1.0 |
| Device | string | Output device name |
| MaxConcurrentStreams | int | 1-64, default 6 |
| HistorySlots | int | ≥0, default 32 |
| MaxReplacementPasses | int | 0-100, default 20 |
| EnableErrorNotifications | bool | default true |
| EnableWarningNotifications | bool | default false |
| TextReplacements | TextReplacement[] | Regex-based substitution rules |
| VoiceChanges | VoiceChangeMapping[] | Hint → voice mappings |
| SoundEffects | SoundEffectMapping[] | Hint → file path + volume mappings |
| UserTags | UserTagDefinition[] | Custom tag definitions |

**Lifecycle:**
- Loaded from `%AppData%\TypeToSquad\config.json` at startup
- Mutated through the Settings Window
- Saved on Settings Window close and app exit
- If the file is missing or corrupt, defaults are used and the file is created

### 2. Message (Transient Entity)

Represents a single user input from summon to discard.

```
Input Popup shown (empty)
  → User types raw text
  → User presses Enter
  → Message is Processed (→ RenderNode tree)
  → RenderNode tree is Synthesized (→ audio bytes via daemon)
  → Audio is Played
  → Message text is saved to History
  → Message is discarded
```

**State:**
- Raw text (string)
- Processed tree (RenderNode) — populated after submission, null before

**Behavior:**
- On submit: validate non-empty, process, synthesize, play, archive to history
- On cancel (Esc): preserve raw text for next summon

### 3. Playback (Transient Entity)

Represents one active or queued audio playback.

**State:**
- Audio data (byte[] or stream)
- Playback state: Playing | Stopped
- Associated Voice (for display)

**Behavior:**
- Start playing through the selected output device
- Stop on request (from Stop hotkey or oldest-stream eviction)
- Auto-dispose when finished

## Value Objects

### HotkeyBinding
```
Modifiers: ModifierKeys (Ctrl, Alt, Shift, Win — flags)
Key: Key (the primary key)
```
Immutable. Serialized as `"Ctrl+Shift+T"` in JSON.

### VoiceInfo
```
Name: string       // e.g., "Microsoft Zira"
Language: string   // e.g., "en-US"
```
Immutable. Queried from the daemon at startup. Never stored in settings — only the selection key is stored.

### VoicePreferences
```
Pitch: double   // 0.0-2.0, default 1.0
Rate: double    // 0.5-6.0, default 1.0
Volume: int     // 0-100, default 100
```

### TextReplacement
```
Pattern: string       // Regex pattern
Substitution: string  // Replacement string
```
Each row is a pair. Applied in order during processing passes.

### VoiceChangeMapping
```
Hint: string     // Tag argument used in [voice hint]
VoiceKey: string // References a Voice by its key
```

### SoundEffectMapping
```
Hint: string      // Tag argument used in [sound hint]
FilePath: string  // Absolute path to .wav/.ogg/.mp3 file
VolumePercent: int // 0-100
```

### UserTagDefinition
```
TagType: string      // The tag name, e.g., "myeffect"
Pattern: string      // Regex applied to the tag argument
Replacement: string  // Substitution
```

### RenderNode
(Already exists — see `src/TypeToSquad/Model/Markup/RenderNode.cs`)
```
Type: RenderNodeType    // Text, SsmlRoot, Voice, Phoneme, Break, Sound, Serial
Children: List<RenderNode>
Attributes: Dictionary<RenderNodeAttribute, string>
```
A node in the processed-message tree. The tree is the intermediate representation between parsing and synthesis.

### MessageSegment
(Already exists — see `src/TypeToSquad/Model/Markup/MessageSegment.cs`)
```
IsValid: bool
IsTag: bool
Text: string
TagType: string
TagArgument: string
```
A single segment produced by the lexer. Plain text segments and tag segments together represent the full original message.

## Domain Services (in Core)

### MessageProcessor
**Signature:** `static RenderNode ProcessMessage(string message)`

Pure function. Stateless (will be made stateless by passing settings + voice storage as parameters instead of reaching into Godot singletons).

Pipeline:
1. `MessageLexer.SegmentMessage(message)` → `List<MessageSegment>`
2. N passes of: User Tag expansion → Text Replacements (up to `MaxReplacementPasses`)
3. `SegmentsToInitialTree(segments)` → RenderNode tree (SSML-like)
4. `ProcessInitialNodeTree(tree)` → normalized tree (pull out Sound/Break nodes, flatten SSML wrappers around plain text, remove empty text nodes, unwrap single-child Serial)

### MessageLexer
**Signature:** `static List<MessageSegment> SegmentMessage(string message)`

Pure function. Stateless already. Splits raw text into segments, handling tag boundaries, nesting detection, and unclosed tag recovery.

## Application Services (in Wpf)

These are the orchestrators that bridge domain logic with infrastructure:

### SynthesisService
- Takes a RenderNode tree
- Decides: plain text → send as text; SSML → send as SSML; Sound → load from file; Serial → chain requests
- Communicates with the daemon via `SpeechDaemon` (named pipe)
- Returns audio bytes (WAV PCM)
- Runs daemon requests on background thread, dispatches callbacks to UI thread

### PlaybackService
- Takes audio bytes, plays via NAudio `WaveOutEvent` or `WasapiOut`
- Manages concurrent playback (enforces `MaxConcurrentStreams`, evicts oldest)
- Exposes `StopAll()` for the Stop hotkey
- Exposes current output device selection

### SettingsService
- Loads `AppSettings` from `config.json` on startup (System.Text.Json)
- Saves `AppSettings` to `config.json` on change
- Provides defaults for missing/corrupt files
- Watches for file changes (optional, v2)

### HotkeyService
- Registers Summon and Stop hotkeys via Win32 `RegisterHotKey`
- Unregisters on app shutdown
- Raises .NET events when hotkeys are pressed
- Re-registers when bindings change

### DaemonProcessService
- Starts `WinRTSpeechSynthServer.exe` on app startup
- Manages named pipe connection
- Queries available voices on startup
- Gracefully terminates daemon on app exit (TerminateRequest → CloseMainWindow → Kill)
- Restarts daemon if it crashes

## Infrastructure (in Wpf)

| Concern | Implementation |
|---|---|
| Audio playback | NAudio (`WaveOutEvent` for output device selection) |
| Global hotkeys | Win32 `RegisterHotKey` / `UnregisterHotKey` via P/Invoke |
| Settings persistence | `System.Text.Json` → `%AppData%\TypeToSquad\config.json` |
| Logging | `Microsoft.Extensions.Logging` → console + file sink |
| Single instance | Named `Mutex` + `WM_COPYDATA` to forward args to existing instance |
| System tray | `System.Windows.Forms.NotifyIcon` (WPF interop) |
| Daemon IPC | Named pipe (`NamedPipeClientStream`), shared `WinRTSpeechSynthServer.Protocol.dll` |

## Ports / Interfaces (in Core)

Interfaces defined in Core, implemented in Wpf:

```csharp
// Audio output
interface IAudioPlayer {
    void Play(byte[] wavData);
    void StopAll();
    string[] GetOutputDevices();
    string CurrentDevice { get; set; }
}

// TTS synthesis
interface ISpeechSynthesizer {
    Task<byte[]> SynthesizeAsync(RenderNode node, VoicePreferences prefs, VoiceInfo voice);
}

// Voice discovery
interface IVoiceProvider {
    Task<VoiceInfo[]> GetInstalledVoicesAsync();
    VoiceInfo DefaultVoice { get; }
}

// Settings persistence
interface ISettingsRepository {
    AppSettings Load();
    void Save(AppSettings settings);
}
```

## Data Flow (end-to-end)

```
User presses Ctrl+Shift+T
  → HotkeyService raises Summon event
  → Input Popup appears (empty or with preserved text)

User types, presses Enter
  → Input Popup hides
  → MessageProcessor.ProcessMessage(rawText, settings, voices)
    → MessageLexer.SegmentMessage
    → N × PerformUserTagsPass + PerformReplacementPass
    → SegmentsToInitialTree
    → ProcessInitialNodeTree
    → RenderNode tree
  → SynthesisService.Synthesize(tree)
    → For each node in tree (depth-first):
      Text/SsmlRoot → daemon SynthesizeRequest → WAV bytes
      Sound → NAudio load from file → WAV bytes
      Serial → chain above, produce AudioStreamPlaylist
    → Audio bytes
  → PlaybackService.Play(bytes)
    → NAudio WaveOutEvent
  → HistoryTracker.Add(text)
  → Input Popup clears, ready for next summon

User presses Ctrl+Shift+X
  → HotkeyService raises Stop event
  → PlaybackService.StopAll()
```
