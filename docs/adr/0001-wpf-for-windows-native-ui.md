# ADR-0001: WPF over Avalonia UI and WinForms

**Date:** 2026-08-05
**Status:** Accepted

## Context

TypeToSquad needs a desktop UI framework for its WPF rewrite. The app is inherently Windows-only because the TTS engine (`WinRTSpeechSynthServer.exe`) wraps the WinRT `Windows.Media.SpeechSynthesis` API, which only exists on Windows. Three frameworks were evaluated:

| Criteria | WPF | Avalonia UI | WinForms |
|---|---|---|---|
| System tray support | Mature (interop with WinForms NotifyIcon) | Third-party library required | Native (NotifyIcon) |
| Global hotkeys | P/Invoke RegisterHotKey (trivial) | Platform-specific code per OS | P/Invoke RegisterHotKey (trivial) |
| Data binding for settings UI | Excellent (DataGrid, DataTemplate, MVVM) | Good (similar to WPF) | Weak (manual DataGridView wiring) |
| .NET 8 support | Full | Full | Full |
| Existing C# code reuse | Direct (same System.Windows.Media types, etc.) | Some adaptation needed | Direct |
| Cross-platform | No (irrelevant — app is Windows-only) | Yes (unused) | No (irrelevant) |

## Decision

Use **WPF (.NET 8)**.

## Rationale

1. **Windows-only is a given.** The daemon uses WinRT APIs. Porting the daemon to non-Windows would require an entirely different TTS backend (e.g., system voices on macOS, espeak on Linux). Cross-platform UI buys nothing when the core engine can't run elsewhere.

2. **System tray and global hotkeys are first-class requirements.** WPF + a WinForms interop `NotifyIcon` handle both with minimal code. Avalonia would require third-party libraries with unknown maintenance trajectories. WinForms could do both natively but lacks WPF's data binding for the settings UI.

3. **Settings UI complexity.** The settings window includes a DataGrid for text replacements, dropdowns for voice selection, and sliders for numeric ranges. WPF's `DataGrid`, `Slider`, `ComboBox` with `DataTemplate` and MVVM binding significantly reduce the code needed compared to WinForms.

4. **Familiarity.** WPF is the standard Windows desktop UI framework. Documentation, community support, and tooling (Visual Studio designer, hot reload) are mature.

## Alternatives

- **Avalonia UI** — Rejected. Cross-platform is unnecessary overhead. System tray requires `H.NotifyIcon` or similar third-party package. Global hotkey handling differs per platform, adding complexity for no benefit.
- **WinForms** — Rejected. Viable for the simple input popup, but the settings UI (DataGrid for text replacement table, tabbed interface, styled controls) would require significantly more manual code. WPF's XAML-based styling also makes the semi-transparent input popup easier to implement.

## Consequences

- The app is locked to Windows (already true due to the daemon).
- WPF's `WindowStyle=None` + `AllowsTransparency=True` enables the semi-transparent popup design.
- System tray uses `System.Windows.Forms.NotifyIcon` in a WPF host — requires a reference to `System.Windows.Forms` and `System.Drawing`.
- Global hotkeys use P/Invoke to `user32.dll` (`RegisterHotKey`, `UnregisterHotKey`) with a `HwndSource` hook for `WM_HOTKEY`.
