# MCP ROM/Tab Lifecycle (open live, close tab, close rom) — Design Spec

**Date:** 2026-06-20
**Status:** Approved (design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (HexManiacAdvance @ 0.5.6.1, branch `mcp-integration`)
**Builds on:** the live-GUI MCP integration (goto, typed write_value, editor ops).

## Goal

Let the MCP manage the ROM/tab lifecycle of a running HexManiacAdvance GUI, where
more than one ROM may be open and a single ROM may have more than one tab:

- **Open** a `.gba` into the running GUI as a new tab (today `open_rom` is
  headless-only).
- **Save** a ROM in a specific tab (today's `save_rom` already targets a tab —
  confirmed, no change needed).
- **Close a tab** (one tab by selector).
- **Close a ROM** (all tabs that share that ROM's in-memory model).

## Background

- `EditorViewModel` holds the open tabs (each an `IViewPort`/`ViewPort`); multiple
  tabs can reference the same `Model` (the same ROM opened twice, or a map/derived
  tab). `EditorViewModel.Open` (`ICommand`, "parameter: file to open") opens a file
  into a new tab; tabs close through the editor's internal tab-removal triggered by
  a tab's close event.
- **Unsaved flag:** `ViewPort.ChangeHistory.HasDataChange` is true when a tab has
  unsaved data changes (it drives the `*` in the tab title). This is the
  authoritative "is this tab dirty" check.
- The automation pipe (`AutomationPipeServer`) already has `editor`, `ResolveTab`,
  `GuiFileSystem()`, `ListTabs`, and runs every handler on the WPF Dispatcher
  thread. `save_rom` already resolves a tab and runs its `Save` live.
- `RomSession` (headless) holds a single ROM/`ViewPort`; the multi-ROM/multi-tab
  concept is inherently a live-GUI feature.

## Tool surface

### `open_rom(path, ...)` — now live + headless
- **Live:** open the `.gba` at `path` into the running GUI as a new tab
  (`editor.Open.Execute(path)` on the Dispatcher thread). Return
  `{ ok, path, index, file, mode: "live" }` where `index` is the new tab's index.
- **Headless (unchanged):** load the single `RomSession` from disk; return today's
  shape (`{ ok, path, length, anchorCount, mode: "headless" }`).
- Routed through the existing `Dispatch` (live params = `{ path }`, no tab
  selector): a reachable GUI handles it as a new-tab open, otherwise the headless
  `session.Load` lambda runs.

### `save_rom(...)` — unchanged
Already saves the resolved tab live (`tab`/`tabFile`); headless writes the loaded
ROM. No change beyond documentation.

### `close_tab(tab = null, tabFile = null, force = false)` — live-only
Close the one resolved tab (default: active tab). If the tab's
`ChangeHistory.HasDataChange` is true and `force` is false →
`{ error: "Tab '<file>' has unsaved changes; pass force=true to discard." }`.
Otherwise remove the tab **without any GUI save prompt** and return
`{ ok, closed: <file>, remaining: <ROM tab count>, mode: "live" }`.
Headless → `{ error: "close_tab requires the live GUI." }`.

### `close_rom(tab = null, tabFile = null, force = false)` — live-only
Close **all tabs whose `Model` is the same as the resolved tab's model** (i.e. every
tab showing that in-memory ROM). If **any** of those tabs has unsaved changes and
`force` is false → error naming the unsaved tab(s). Otherwise remove them all
(no prompt) and return `{ ok, closedCount, remaining, mode: "live" }`.
Headless → `{ error: "close_rom requires the live GUI." }`.

## Close mechanism (no modal prompt)

The GUI's normal close path can show a "save before closing?" dialog via the
real `IFileSystem`, which an MCP caller cannot answer. So close MUST NOT trigger
that prompt:

- Check `HasDataChange` ourselves first; refuse unless `force`.
- When clearing/forcing, remove the tab through a non-prompting path. The
  implementation will (in priority order) either: mark the tab's history as saved
  so its `Close` performs no prompt, or invoke the editor's tab-removal directly.
  The implementation plan pins the exact API after reading `ViewPort.Close` /
  `ChangeHistory` (e.g. an `IsSaved`/tag-saved setter or an internal `RemoveTab`);
  the requirement is: **no dialog, tab gone, no disk write.**

## Live / headless wiring

- New pipe methods in `AutomationPipeServer`: `open_rom` (live open as new tab),
  `close_tab`, `close_rom` — each on the Dispatcher thread, operating on
  `editor`/tabs.
- `RomTools`: extend `open_rom` to prefer the live pipe (new-tab open) and fall
  back to the existing headless `session.Load`; add `close_tab`/`close_rom` tools
  (live-only; headless fallback returns the live-only error). All results carry
  `mode` via the existing `Stamp`.
- Tab resolution reuses `ResolveTab` (index / filename substring / active tab).
  `close_rom` groups by `vp.Model` reference equality.

## Error handling

`{ error }` on every failure, never a crash:
- `open_rom` live: file not found / not a valid ROM → surface the GUI's load error.
- `close_tab`/`close_rom`: no open tab; unsaved-without-force (names the tab);
  live-only in headless.
- Closing the last tab is allowed (GUI may then show its start screen) — not an error.

## Components & boundaries

- **`AutomationPipeServer`** (WPF) — owns the three new handlers; the only place
  that touches `editor` tab collection + `HasDataChange` + the no-prompt removal.
- **`RomTools`** (MCP) — `open_rom` live/headless routing; `close_tab`/`close_rom`
  tools; `mode` stamping. No new Core logic (lifecycle is GUI/session state, not
  `IDataModel` math), so `RomAutomation` is unchanged.

## Testing

- **Live gate** (`test/mcp-live-smoke.sh`): with the test ROM open, `open_rom` a
  second copy → `list_open_roms` shows 2 tabs; `close_tab` the new one → back to 1;
  edit a value (dirty), `close_tab` without `force` → error, with `force:true` →
  closes; assert each result's `mode == "live"`.
- **Headless gate** (`test/mcp-smoke.sh`, stays ALL GREEN): `close_tab` and
  `close_rom` return the live-only error; `open_rom` headless behavior unchanged
  (the existing open/read/save/reload assertions stay green).

## Out of scope (v1)

Opening non-`.gba` files; `save_as` to a new path (separate follow-up if wanted);
reordering tabs; headless multi-ROM (RomSession stays single-ROM); responding to a
genuine GUI save dialog (we avoid triggering it entirely).

## Build impact

No new projects/SDK changes; build commands unchanged. No Core changes expected
(WPF + MCP only).
