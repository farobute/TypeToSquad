# ADR-0002: NAudio for Audio Playback

**Date:** 2026-08-05
**Status:** Accepted

## Context

The Godot version of TypeToSquad uses Godot's built-in audio engine (`AudioStreamPlayer`, `AudioStreamWav`, `AudioStreamOggVorbis`, `AudioStreamMP3`, `AudioStreamPlaylist`, `AudioServer`) for audio output. The WPF rewrite needs a pure .NET audio playback library with these requirements:

- Play PCM WAV data from in-memory byte arrays (the daemon returns WAV bytes — writing to temp files would be wasteful)
- Play pre-recorded sound effects from `.wav`, `.ogg`, and `.mp3` files
- Support multiple concurrent playback streams with per-stream volume control
- Support output device selection
- Low CPU/memory overhead (the app is a background tray application)
- MIT or similarly permissive license

Four options were evaluated:

| Library | WAV from memory | OGG/MP3 from file | Output device selection | Concurrent streams | Overhead |
|---|---|---|---|---|---|
| **NAudio** | Yes | Yes | Yes (WaveOut, Wasapi) | Yes | ~300KB DLL |
| System.Media.SoundPlayer | Yes (limited) | No | No | No | Built-in |
| Windows.Media.Playback | No (file/stream only) | Yes | System default only | Limited | Built-in |
| SharpDX / XAudio2 | Yes | No (WAV only) | Yes | Yes | Larger, game-oriented |

## Decision

Use **NAudio** for all audio playback.

## Rationale

1. **WAV from memory is the primary use case.** The daemon returns PCM WAV bytes. NAudio's `RawSourceWaveStream` or `WaveFileReader` over a `MemoryStream` handles this directly, without writing temporary files to disk.

2. **Output device selection is required.** Users need to route TTS output to virtual audio cables (VB-Audio Cable, Voicemeeter). NAudio's `WaveOutEvent` accepts a device number, and `WasapiOut` accepts a device GUID — both support enumeration via `WaveOut.DeviceCount` / `MMDeviceEnumerator`.

3. **Concurrent stream management.** The app plays multiple utterances concurrently (up to `MaxConcurrentStreams`, default 6). NAudio supports multiple simultaneous `WaveOutEvent` or `WasapiOut` instances, each with independent volume. Oldest-stream eviction is straightforward at the application level.

4. **Proven, mature library.** NAudio has been the standard .NET audio library since 2007. MIT license. Actively maintained. Widely used in both open-source and commercial projects.

5. **Minimal footprint.** A single ~300KB DLL with no native dependencies (unless WASAPI is used, which is a Windows system API). The Godot audio engine, by comparison, includes a full mixer, bus system, effects pipeline, and multi-platform backends — all unused overhead for this use case.

## Alternatives

- **System.Media.SoundPlayer** — Rejected. Only supports WAV from file paths (not memory streams reliably), no concurrent playback, no device selection, no OGG/MP3.
- **Windows.Media.Playback (WinRT)** — Rejected. Designed for media files, not memory buffers. API is async-heavy and oriented toward foreground media apps. Output device selection requires system-level routing.
- **SharpDX / XAudio2** — Rejected. Powerful but game-oriented. Larger dependency footprint. Overkill for simple WAV/OGG/MP3 playback. SharpDX maintenance has also been sporadic.

## Consequences

- `NAudio` NuGet package added to `TypeToSquad.Wpf` (and `TypeToSquad.Core` for the `IAudioPlayer` interface).
- `PlaybackService` wraps NAudio APIs; domain code only depends on the `IAudioPlayer` port interface.
- Sound effects now rely on NAudio instead of Godot's `AudioStreamOggVorbis` / `AudioStreamMP3`. NAudio's OGG support comes from `NAudio.Vorbis` (or `NVorbis`). MP3 support is via `NAudio.Lame` or the built-in ACM codec. This may require an additional NuGet dependency for OGG (e.g., `NAudio.Vorbis`).
- Output device enumeration API is slightly different between WaveOut (integer device numbers) and WASAPI (MMDevice GUIDs). WASAPI is recommended for lower latency and better device matching, but adds a dependency on the Windows Core Audio API (system-level, no NuGet needed).
