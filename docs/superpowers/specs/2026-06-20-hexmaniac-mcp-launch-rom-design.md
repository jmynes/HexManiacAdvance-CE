# MCP `launch_rom` (play button) — Design Spec

**Date:** 2026-06-20
**Status:** Approved (user approved the design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (branch `mcp-integration`)

## Goal

Let the MCP launch the open/loaded ROM the way HexManiacAdvance's play button does —
shell-open it with the OS default program (an emulator) — and report which file was
launched.

## Background

HMA's play button is `EditorViewModel.RunFile`: enabled only when the tab is
**saved** (`!ChangeHistory.HasDataChange`), and it calls
`fileSystem.LaunchProcess(FullFileName)`. `WindowsFileSystem.LaunchProcess` →
`NativeProcess.Start(fullPath)` → `Process.Start(new ProcessStartInfo(path){
UseShellExecute = true })` — i.e. open the `.gba` with whatever program is
registered for it (it has a `try/catch (Win32Exception)` for "no associated
program"). There is no configured emulator path. `NativeProcess.Start` lives in
`HavenSoft.HexManiac.Core` (SystemExtensions).

## Tool surface — `launch_rom(tab = null, tabFile = null, force = false)`

Both modes:
1. Resolve the ROM's on-disk path: headless `session.RomPath`; live the resolved
   tab's `FullFileName`. If empty or the file doesn't exist on disk → error.
2. If the ROM has **unsaved edits** (`ChangeHistory.HasDataChange`) and `force` is
   false → `{ error: "ROM has unsaved changes; save first (save_rom), or pass
   force=true to launch the last-saved file on disk." }` (no launch). Mirrors the
   play button being disabled while dirty (launching would run a stale file).
3. Otherwise launch (shell-open) the on-disk file:
   - live (pipe): `GuiFileSystem().LaunchProcess(path)`.
   - headless: `NativeProcess.Start(path)`, wrapped so a `Win32Exception`
     ("no program associated with .gba") returns a clear error instead of throwing.
4. Return `{ ok: true, launched: "<full path>", mode }`. "Which one we launched" =
   `launched`.

It's fire-and-forget shell-open; OS file associations don't yield a reliable PID,
so the response identifies the launch by path, not process id.

## Wiring

- New pipe method `launch_rom` in `AutomationPipeServer`: resolve tab → check
  `vp.ChangeHistory.HasDataChange` / `force` → `GuiFileSystem().LaunchProcess(
  vp.FullFileName)` → `{ ok, launched, ... }`.
- MCP `launch_rom` tool routes via `Dispatch`; headless lambda checks
  `session.RequireViewPort().ChangeHistory.HasDataChange` and uses
  `NativeProcess.Start(session.RomPath)`. `mode` stamped. Tool count 22 → 23.

## Error handling

`{ error }`, never crash: no ROM loaded / tab with no file; file not on disk;
unsaved + not forced; launch failure (no associated program / Win32Exception).

## Testing (gate-safe — MUST NOT spawn an emulator in CI)

The happy path spawns an external program, so it is **not** automated.
- **Headless gate** (`test/mcp-smoke.sh`, ALL GREEN; tool count → 23): open the
  throwaway `firered.edited.gba`, `write_value` to make it dirty, then `launch_rom`
  (no force) → asserts an "unsaved changes" error and **no process is spawned**.
  (Do not call `launch_rom force=true` or on a clean ROM in the gate — that would
  shell-open a `.gba`.)
- **Manual / live:** the actual launch is verified by the user in the live GUI
  (with an emulator associated to `.gba`).

## Components & boundaries

- **`RomTools`** (MCP) — `launch_rom` tool + headless launch (`NativeProcess.Start`
  in a try/catch).
- **`AutomationPipeServer`** (WPF) — `launch_rom` case (`GuiFileSystem().LaunchProcess`).
- No Core changes (reuses `NativeProcess.Start` / `IFileSystem.LaunchProcess`).

## Out of scope

A configurable emulator path/args (HMA itself just shell-opens); tracking the
emulator process lifetime; auto-saving before launch (that's `force` + manual save).
