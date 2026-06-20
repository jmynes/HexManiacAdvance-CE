# MCP Editor Operations (undo/redo, select, copy/paste) — Design Spec

**Date:** 2026-06-20
**Status:** Approved (design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (HexManiacAdvance @ 0.5.6.1, branch `mcp-integration`)
**Builds on:** the live-GUI MCP integration (`goto`, `list_shortcuts`, typed `write_value`).

## Goal

Let the MCP drive the editor operations a person uses in the HexManiacAdvance GUI:
undo/redo, table selection (a row, multiple rows, or a whole table), row
duplication (copy/paste), and parity with the GUI's real clipboard. Each is a
separate MCP tool (one verb per tool), routed live to the open GUI tab when a GUI
is running, else headless, with a `mode` field on every result.

## Background

These map to existing commands on `ViewPort` / `EditorViewModel`
(`src/HexManiac.Core/ViewModels/`):

- `ViewPort.Undo` / `ViewPort.Redo` (`ICommand`) — the change-history stack
  (same one Ctrl+Z/Y use).
- `ViewPort.SelectionStart` / `SelectionEnd` (`Point`, public set) — selection;
  `SelectAll` exists too.
- `ViewPort.Copy(IFileSystem)` / `Paste(IFileSystem)` — clipboard ops over the
  current selection.
- `ModelArrayElement.Start` / `.Length` — an element's address and byte length,
  for computing row ranges and raw bytes.

Edits already flow through a change token (`vp.CurrentChange` live,
`session.Token` headless), so token-based writes are visible + undoable.

## Tool surface (all under `RomTools`, routed via the existing `Dispatch`)

### Component 1 — History (live + headless)
- **`undo(count = 1, tab?, tabFile?)`** — apply up to `count` undo steps to the
  target tab's change history, stopping early if the stack empties. Returns
  `{ ok, applied, mode }` (`applied` = steps actually undone).
- **`redo(count = 1, tab?, tabFile?)`** — same for redo.

### Component 2 — Selection (live-only)
- **`select(table, index = null, count = 1, tab?, tabFile?)`** — select `count`
  rows beginning at element `index`; if `index` is null, select the whole table
  (index 0 .. last). Sets `SelectionStart`/`SelectionEnd` to the element byte
  range and scrolls it into view. Returns `{ ok, table, index, count, address, mode }`.
  Headless → `{ error: "select requires the live GUI (no view to select in headless mode)." }`.

### Component 3 — Self-contained row copy/paste (live + headless)
- **`copy_rows(table, index, count = 1, tab?, tabFile?)`** — capture the raw bytes
  of elements `[index, index+count)`; return `{ ok, table, index, count, bytes, mode }`
  where `bytes` is a hex string. The server caches the last copy (bytes + element
  length) so `paste_rows` can omit `data`.
- **`paste_rows(table, index, data = null, tab?, tabFile?)`** — write bytes onto
  the destination element(s) starting at `index`, through the change token
  (undoable + visible). `data` is a hex string; if null, use the last `copy_rows`
  result. Validates the byte length is a whole multiple of the destination table's
  element length and fits within the table. Returns `{ ok, table, index, count, mode }`.

### Component 4 — GUI clipboard parity (live-only, Phase 2)
- **`clipboard_copy(tab?, tabFile?)`** — run `ViewPort.Copy(fileSystem)` on the
  current selection; return `{ ok, text, mode }` (the copied clipboard text).
- **`clipboard_paste(tab?, tabFile?)`** — run `ViewPort.Paste(fileSystem)` at the
  current selection. Returns `{ ok, mode }`. Shares the OS clipboard with manual
  Ctrl+C/V.

## Live / headless wiring

- New pipe methods in `AutomationPipeServer`: `undo`, `redo`, `select`,
  `copy_rows`, `paste_rows`, `clipboard_copy`, `clipboard_paste` — each on the
  Dispatcher thread, operating on the resolved tab's `ViewPort`.
- `RomTools` adds the matching tools, each routing through `Dispatch` (live params
  include the tab selector + op args; headless fallback as below), stamping `mode`.
- **Headless behavior:** `undo`/`redo`/`copy_rows`/`paste_rows` operate on the
  loaded `RomSession`'s `ViewPort`/model. `select` and the `clipboard_*` tools are
  live-only and return a clear error in headless mode.
- Shared, view-independent logic (capturing element bytes, writing pasted bytes,
  computing a row's address range) lives in `HexManiac.Core` (e.g.
  `RomAutomation`) so both backends share one implementation; selection and
  clipboard, which need the `ViewPort`, stay in the pipe handler / a thin live path.

## Undo/redo coverage note

`undo`/`redo` drive `ViewPort.Undo`/`Redo`, which act on that tab's change
history. Live edits (`write_value`, `run_script`) already go through the GUI tab's
history, so they undo cleanly. Implementation MUST verify headless `write_value`
edits are on the same history the headless `undo` targets; if the headless write
token and the headless `ViewPort` history differ, document `undo`/`redo` as
live-primary and make the headless path operate on whatever history the headless
writes use (no silent no-op).

## Error handling

Every failure returns `RomAutomation.Err`-style `{ error }` — never a crash:
- Unknown `table`; `index`/`count` out of range; no open tab (live).
- `paste_rows`: `data` not valid hex; byte length not a multiple of the element
  length; paste would exceed the table — each a distinct message.
- `select` / `clipboard_*` in headless → the live-only error.
- Nothing to undo/redo → `{ ok, applied: 0 }` (not an error).

## Components & boundaries

- **`RomAutomation`** (Core) — `CopyRows(model, table, index, count) -> bytes`,
  `PasteRows(model, token, table, index, bytes) -> result`, and a row-range helper
  `RowRange(model, table, index, count) -> (start, length)`. Pure, testable.
- **`RomTools`** (MCP) — seven new tools; JSON arg coercion; live/headless routing;
  the last-copy cache (a private static buffer).
- **`AutomationPipeServer`** (WPF) — seven new pipe cases; `undo`/`redo`/`select`/
  `clipboard_*` use the `ViewPort`; `copy_rows`/`paste_rows` call the shared Core
  logic on the tab's model + `CurrentChange`.

## Testing

- **Core unit tests** (`HexManiac.Tests`, no GUI): write→`undo`→original value,
  then `redo`→new value; `copy_rows` then `paste_rows` to a different index makes
  the destination element equal the source; `paste_rows` with a mismatched byte
  length returns an error; `RowRange` computes the expected address/length.
- **Headless gate** (`test/mcp-smoke.sh`, stays ALL GREEN): an `undo` after a
  write reverts it; a `copy_rows`+`paste_rows` round-trip; `select` returns the
  live-only error.
- **Live gate** (`test/mcp-live-smoke.sh`, LIVE GREEN): `select` sets the expected
  selection range; `clipboard_copy`→`clipboard_paste` round-trips; `undo`/`redo`
  report `mode:"live"`.

## Phasing

1. **Phase 1 (Components 1–3):** undo/redo, select, self-contained copy/paste —
   deterministic and testable; covers undo/redo + selection + real row duplication.
2. **Phase 2 (Component 4):** GUI clipboard parity (`clipboard_copy`/`paste`).

The implementation plan orders tasks accordingly.

## Out of scope (v1)

Cross-table paste with differing element layouts; multi-tab clipboard; partial-row
(single-field) copy; selection of arbitrary byte ranges unrelated to a table
element.

## Build impact

No new projects/SDK changes. Shared logic stays in Core (net6.0); both WPF (net6.0)
and MCP (net8.0) reference it. Build commands unchanged.
