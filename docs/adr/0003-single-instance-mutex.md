# ADR-0003: Single Instance via Mutex + WM_COPYDATA

**Date:** 2026-08-05
**Status:** Accepted

## Context

TypeToSquad is a system-tray application. Double-clicking the executable again should not spawn a second process — it should activate the existing instance's input popup. A second instance would also contend for the daemon process (named pipe name is unique per spawn, so a second app instance would start a second daemon — wasteful and potentially confusing).

Three inter-process communication (IPC) strategies were considered for passing "show the popup" from the second process to the first:

| Approach | Complexity | Reliability | Notes |
|---|---|---|---|
| Mutex + WM_COPYDATA | Low | High | Windows standard for single-instance apps |
| Mutex + Named Pipe | Medium | High | Requires a listener thread in the main process |
| Mutex + TCP loopback | Medium | Medium | Port conflicts possible; firewall may interfere |

## Decision

Use a **named `Mutex`** for instance detection and **`WM_COPYDATA`** for forwarding activation commands from the second process to the first.

## Rationale

1. **Mutex is the standard single-instance guard on Windows.** A named `Mutex` (e.g., `"TypeToSquad_SingleInstance"`) is created on startup. If `WaitOne(0)` fails (already exists), the process knows another instance is running.

2. **WM_COPYDATA is the simplest IPC for this use case.** The need is trivial: "tell the existing instance to show the input popup." `WM_COPYDATA` is a window message that carries a block of bytes between processes. The WPF main window's `HwndSource` hook receives it in-process on the UI thread — no extra threads, no serialization complexity, no port conflicts.

3. **No additional dependencies.** Both `Mutex` and `WM_COPYDATA` are in the .NET BCL / Win32 API. No NuGet packages needed.

4. **Second instance exits immediately.** After forwarding the activation message, the second process terminates. The user never sees a second window.

## Alternatives

- **Named Pipe** — Rejected. Requires a dedicated listener thread in the main process. More code than `WM_COPYDATA`. Appropriate for richer IPC needs (bidirectional, large data), but overkill for a one-way "wake up" signal.
- **TCP Loopback** — Rejected. Port selection needs care (hardcoded port could conflict). Firewall may prompt on first run. Same thread-management overhead as named pipes.

## Implementation Sketch

```
App.Startup:
  mutex = new Mutex(true, "TypeToSquad_SingleInstance", out bool isFirst)
  if not isFirst:
      FindWindow → Send WM_COPYDATA("SHOW") → Environment.Exit(0)
  else:
      Register HwndSource hook for WM_COPYDATA → continue normal startup

On WM_COPYDATA("SHOW"):
  Show input popup, activate window
```
