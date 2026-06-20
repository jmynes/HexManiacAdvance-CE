# MCP `save_rom` Overwrite Guard — Design Spec

**Date:** 2026-06-20
**Status:** Approved (user approved the design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (branch `mcp-integration`)

## Goal

Stop `save_rom` from silently overwriting the loaded/open ROM. This footgun
corrupted the test fixture (a no-`outPath` headless `save_rom` overwrote
`test/roms/firered.gba`). Overwriting in place must become an explicit opt-in.

## Background (current behavior — the bug)

- **Headless** (`RomTools.SaveRomHeadless`): `target = outPath ?? session.RomPath`
  → with no `outPath` it writes over the loaded source ROM.
- **Live** (`AutomationPipeServer` `save_rom`): `vp.Save.Execute(GuiFileSystem())`
  → saves the open tab to its own file (also an in-place source overwrite), and
  ignores `outPath` entirely.

## Tool surface — `save_rom(outPath = null, overwrite = false, tab = null, tabFile = null)`

A new `overwrite` (bool, default false). Behavior in **both** modes:

1. **`outPath` provided → save a copy to `outPath`** (source untouched):
   - headless: `File.WriteAllBytes(outPath, model.RawData)` (today's copy behavior).
   - live: `File.WriteAllBytes(outPath, vp.Model.RawData)` (new — a raw byte copy of
     the tab's ROM; no GUI dialog, no metadata sidecar written).
2. **No `outPath` + `overwrite == true` → save over the source:**
   - headless: commit changes, `File.WriteAllBytes(session.RomPath, model.RawData)`.
   - live: `vp.Save.Execute(GuiFileSystem())` (today's tab-save to its file).
3. **No `outPath` + `overwrite == false` → guard error (no write):**
   `{ "error": "Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source." }`

Result shape: `{ ok: true, path|saved: "<file>", overwrote: <bool>, mode }`
(`overwrote` = true only in case 2). The guard error carries `mode`.

## Live / headless wiring

- **`RomTools.SaveRom`** gains `bool overwrite = false`. It forwards `outPath` and
  `overwrite` in the live params and to the headless fallback. The headless lambda
  is the updated `SaveRomHeadless(session, outPath, overwrite)`.
- **`AutomationPipeServer` `save_rom`** reads `outPath` (string) + `overwrite`
  (bool, via the existing `Bool` helper) and:
  - `outPath` non-empty → `File.WriteAllBytes(outPath, vp.Model.RawData)`; return
    `{ ok, path: outPath, overwrote: false }`.
  - else `overwrite` → `vp.Save.Execute(GuiFileSystem())`; return `{ ok, saved:
    vp.FullFileName ?? vp.Name, overwrote: true }`.
  - else → `AutoResponse(false, null, "Refusing to overwrite ...")`.
- `SaveRomHeadless`: `outPath` non-empty → write there (`overwrote:false`); else
  `overwrite` → write `session.RomPath` (`overwrote:true`); else → `Err(guard)`.
- Every result carries `mode` via `Dispatch`/`Stamp`.

## Backward compatibility / gates

- The headless smoke gate's `save_rom` call passes `outPath` → still a copy-save,
  unchanged (`overwrote:false`). The result gains an `overwrote` field the gate
  doesn't assert against. No existing assertion regresses.
- No existing call relies on the no-`outPath` silent overwrite.

## Error handling

`{ error }` (or guard message), never a crash: no open tab (live); guard (no
outPath, no overwrite); headless overwrite with a null `session.RomPath`
(shouldn't happen once loaded — return a clear error if it does).

## Testing (gate-safe — MUST NOT overwrite the real source ROM)

- **Headless gate** (`test/mcp-smoke.sh`, stays ALL GREEN):
  - existing `outPath` save stays green (copy).
  - add: `save_rom` with **no args** → error contains "Refusing to overwrite".
  - add: `open_rom` the throwaway `test/.tmp/firered.edited.gba`, then `save_rom
    overwrite:true` (no outPath) → `ok:true, overwrote:true` (overwrites only that
    temp copy — never `test/roms/firered.gba`).
- **Live gate** (`test/mcp-live-smoke.sh`, LIVE GREEN):
  - `save_rom outPath="<temp>.gba"` → `ok:true`, the temp file is created.
  - `save_rom` with **no args** → error "Refusing to overwrite".
  - **No** `overwrite:true` live test (it would overwrite the GUI's source ROM).

## Components & boundaries

- **`RomTools`** (MCP) — `SaveRom` tool + `SaveRomHeadless` overwrite logic.
- **`AutomationPipeServer`** (WPF) — `save_rom` case: outPath copy / overwrite /
  guard. Reuses `Bool`, `GuiFileSystem`, `ResolveTab`.
- No Core changes.

## Out of scope

Writing the metadata sidecar (`.toml`) alongside a live `outPath` copy; backup
rotation; changing HMA's own GUI save behavior.
