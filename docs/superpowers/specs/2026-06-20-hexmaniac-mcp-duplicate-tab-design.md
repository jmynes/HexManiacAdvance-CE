# MCP `duplicate_tab` (Ctrl+T — second tab on the same ROM) — Design Spec

**Date:** 2026-06-20
**Status:** Approved (design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (HexManiacAdvance @ 0.5.6.1, branch `mcp-integration`)
**Builds on:** the ROM/tab lifecycle tools (`open_rom` live, `close_tab`, `close_rom`).

## Goal

Let the MCP open a second tab on the **same** in-memory ROM (the GUI's Ctrl+T /
"Duplicate Current Tab"), so a single ROM can have multiple tabs — which makes
`close_rom` close all of them at once. Today MCP-opened ROMs each get their own
model, so this multi-tab-per-ROM case never arises.

## Background

`Ctrl+T` → `EditorViewModel.DuplicateCurrentTab` → `SelectedTab.Duplicate()` →
`ViewPort.CreateDuplicate()` (`ViewPort.cs:859`):

```csharp
public ViewPort CreateDuplicate() {
   var child = new ViewPort(FileName, Model, dispatcher, Singletons, mapper?.Tutorials,
      mapper?.FileSystem, PythonTool, history, mapper?.Templates);
   child.selection.GotoAddress(scroll.DataIndex);
   return child;
}
```

The duplicate is a new `ViewPort` over the **same `Model`** (and the same
`history`), so the two tabs are views of one ROM. `EditorViewModel.Add(ITabContent)`
adds a tab and selects it; its "another tab shares this model" check means the
duplicate doesn't pop the goto start screen. `ViewPort.CanDuplicate` reports
whether a tab supports duplication.

The automation pipe already has `editor`, `ResolveTab`, `Ok`, `NoTab`, and runs on
the Dispatcher thread; `RomTools` routes tools via `Dispatch` with `mode` stamping.

## Tool surface

### `duplicate_tab(tab = null, tabFile = null)` — live-only
- Resolve a tab (default: active). If `!vp.CanDuplicate` → `{ error: "Tab '<file>'
  cannot be duplicated." }`.
- `var child = vp.CreateDuplicate(); editor.Add(child);` — a second tab on the same
  `Model`/`history`.
- Return `{ ok: true, index, file, mode: "live" }` where `index` is the new tab's
  index and `file` its display name (same file as the source).
- Headless → `{ error: "duplicate_tab requires the live GUI." }`.

## Live / headless wiring

- New pipe method `duplicate_tab` in `AutomationPipeServer`: `ResolveTab` →
  `CanDuplicate` guard → `CreateDuplicate` + `editor.Add` → return the new tab
  index (scan `editor` for reference-equality, as `open_rom` does).
- `RomTools` adds the `duplicate_tab` tool routed via `Dispatch`; headless lambda
  returns the live-only `RomAutomation.Err`. Result carries `mode`. Tool count
  19 → 20.

## Interaction with `close_rom`

`close_rom` groups tabs by `ReferenceEquals(v.Model, vp.Model)`. After
`duplicate_tab`, the original and the duplicate share one `Model`, so `close_rom`
on either closes **both** (`closedCount: 2`). `close_tab` still closes just one.
Because the duplicate shares `history`, the unsaved-change guard sees the same
`HasDataChange` for both — consistent.

## Error handling

`{ error }`, never a crash: no open tab; tab not duplicatable (`CanDuplicate`
false); live-only in headless.

## Components & boundaries

- **`AutomationPipeServer`** (WPF) — the `duplicate_tab` handler; reuses
  `CreateDuplicate`/`Add`. No new Core logic.
- **`RomTools`** (MCP) — the `duplicate_tab` tool; live/headless routing; `mode`.

## Testing

- **Live gate** (`test/mcp-live-smoke.sh`): `duplicate_tab` on the open ROM →
  `list_open_roms` shows two tabs with the same file → `close_rom` returns
  `closedCount` ≥ 2 (both closed). Assert `mode == "live"`.
- **Headless gate** (`test/mcp-smoke.sh`, stays ALL GREEN): `duplicate_tab`
  returns the live-only error; tool count assertion 19 → 20.

## Out of scope (v1)

Duplicating into a specific position; duplicating map/image tabs (only what
`CanDuplicate`/`CreateDuplicate` already support); deduping `open_rom` by path.

## Build impact

No new projects/SDK changes; WPF + MCP only; no Core changes.
