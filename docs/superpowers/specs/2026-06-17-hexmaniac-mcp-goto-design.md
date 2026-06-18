# Live GUI Goto / Navigation — Design Spec

**Date:** 2026-06-17
**Status:** Approved (design). Extends the live GUI ⇄ MCP integration.
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (HexManiacAdvance @ 0.5.6.1, branch `mcp-integration`)
**Builds on:** `2026-06-17-hexmaniac-mcp-live-gui-design.md`

## Goal

Let the MCP server navigate the **running HexManiacAdvance GUI** to a chosen
location — by friendly shortcut label (e.g. `Pokemon`), anchor name
(e.g. `data.pokemon.stats`), or raw address (e.g. `0x1A2B3C`) — the same way the
GUI's "Goto:" shortcut buttons and goto bar already work. Add a companion
read-only method to discover the available shortcut labels (which vary per ROM /
metadata).

This is purely additive to the existing live/headless backends.

## Background

The GUI's start-screen "Goto:" buttons (Pokemon, Trainers, Moves, Items, Maps)
are `GotoShortcutModel { ImageAnchor, GotoAnchor, DisplayText }` entries exposed
as `IDataModel.GotoShortcuts`. Clicking one runs `ViewPort.Goto.Execute(anchor)`
(see `ViewPort.cs:311` and `EditorViewModel.CreateGotoShortcuts`). `Goto.Execute`
already accepts an anchor name **or** a hex address. The automation pipe today
exposes `list_tabs`, `list_tables`, `read_table`, `write_value`, `export_table`,
`run_script`, `save_rom` — but **no navigation method**, so the GUI's view cannot
be driven from MCP.

## Non-negotiable constraint: headless still works

`goto` is meaningful only when a GUI view exists. In headless mode it returns a
clear error (it does not crash or silently no-op). `list_shortcuts` works in both
modes (it only reads `model.GotoShortcuts`). The headless gate
(`test/mcp-smoke.sh`) stays ALL GREEN.

## Tool surface

### MCP (`src/HexManiac.Mcp/RomTools.cs`)

- **New `goto(target, tab? = null, tabFile? = null)`** — *live-only*.
  Forwards to the GUI pipe `goto` method. Headless fallback returns
  `{ "error": "goto requires the live GUI (no view to navigate in headless mode)." }`.
  Result carries `mode: "live" | "headless"`.
- **New `list_shortcuts(tab? = null, tabFile? = null)`** — works in both modes.
  Live: forwards to the pipe. Headless: reads `session.Require().GotoShortcuts`.
  Returns `{ count, shortcuts: [{ display, anchor }], mode }`.

Both route through the existing `Dispatch`/`Stamp` helpers so the `mode` field is
stamped uniformly.

### GUI pipe (`src/HexManiac.WPF/AutomationPipeServer.cs`)

Two new cases in `Handle` (each already wrapped in `Dispatcher.Invoke`):

- **`goto`** — params `{ target: string, tab? }`. Resolves the tab via the
  existing `ResolveTab`, resolves the target (below), then calls
  `vp.Goto.Execute(resolved)`. Returns `{ ok: true, target, resolved, tab }`
  where `tab` is the resolved tab's file/name.
- **`list_shortcuts`** — params `{ tab? }`. Returns
  `{ count, shortcuts: [{ display = DisplayText, anchor = GotoAnchor }] }` from
  `vp.Model.GotoShortcuts`.

## Target resolution

Given `target` (a string), on the Dispatcher thread:

1. **Shortcut label:** case-insensitive match against
   `model.GotoShortcuts[].DisplayText`. On match, the resolved value is that
   entry's `GotoAnchor`.
2. **Otherwise:** pass `target` through unchanged — `Goto.Execute` handles anchor
   names and hex addresses (`0x...` or bare hex) natively.

**Validate before executing** (clean failures, no silent no-op): if the resolved
value is not a matched shortcut, confirm it resolves —
`model.GetAddressFromAnchor(new ModelDelta(), -1, resolved) != Pointer.NULL`, or
the string parses as a hex address. If neither, return
`{ "error": "Unknown goto target '<target>'. Use list_shortcuts, or a valid anchor/address." }`
**without** calling `Goto.Execute`. (Address parsing mirrors how `Goto.Execute`
already accepts addresses; the validation reuses the same acceptance rule so we
never reject a target the command would have accepted.)

## Data flow (goto)

```
Claude → MCP goto tool → GuiBridge.TryCall("goto", { target, tab })
  → named pipe → Dispatcher.Invoke → ResolveTab → resolve target → validate
  → vp.Goto.Execute(resolved)
  → { ok, target, resolved, tab } → Stamp(mode:"live") → Claude
```

`ResolveTab` (index / filename substring / selected tab) and the
`Dispatch`/`Stamp` helpers already exist; this feature adds no new plumbing,
only two handlers and two tools.

## Components & boundaries

- **`AutomationPipeServer.Handle`** — gains `goto` and `list_shortcuts` cases.
  Depends on `ViewPort.Goto`, `ViewPort.Model.GotoShortcuts`, `ModelDelta`,
  `Pointer.NULL`. Single responsibility unchanged: marshal a pipe request onto
  the UI thread and run it against a tab.
- **`RomTools`** — gains `goto` and `list_shortcuts` MCP tools, mirroring the
  existing tool pattern (live via `Dispatch`, headless fallback, `mode` stamp).
- **Optional Core helper** — `RomAutomation.ListShortcuts(IDataModel)` returning
  the `[{ display, anchor }]` list, so the headless `list_shortcuts` and the pipe
  handler share one implementation. `goto` navigation stays in the WPF layer
  because `Goto` is a `ViewPort` command, not model logic.

## Error handling

- No open ROM tab → `{ error: "No open ROM tab." }` (existing pattern).
- `goto` in headless mode → live-only error (above), `mode:"headless"`.
- Unknown/unresolvable `goto` target → error (above), no `Goto.Execute` call.
- Pipe unreachable mid-call → existing `GuiBridge` behavior (falls back to
  headless, which for `goto` yields the live-only error).

## Testing

- **Live gate** (`test/mcp-live-smoke.sh`, extend): with the GUI open on the test
  ROM —
  - `list_shortcuts` result includes a `display == "Pokemon"` entry and
    `mode == "live"`.
  - `goto` with `target:"Pokemon"` returns `ok:true`, `mode:"live"`, and a
    non-empty `resolved` anchor.
- **Headless gate** (`test/mcp-smoke.sh`, must stay ALL GREEN): with no GUI —
  - `goto` returns the live-only error and `mode:"headless"`.
  - `list_shortcuts` returns the shortcut list (count ≥ 1 for the test ROM) and
    `mode:"headless"`.
- **Manual:** after rebuild + relaunch, MCP `goto "Pokemon"` visibly switches the
  GUI window to the Pokémon table view.

## Out of scope (v1)

Opening a brand-new tab/file from MCP; multi-target navigation; scrolling to a
specific row/field within a table beyond what `Goto.Execute(anchor)` lands on;
any change to the existing tools.

## Build & deployment impact

- No new projects or SDK changes; build commands unchanged
  (GUI: `dotnet build -c Release -p:PlatformTarget=x64`;
  MCP: `(cd src/HexManiac.Mcp && dotnet build -c Release)`).
- Activating the feature against the running GUI requires closing the current
  GUI instance (its exe is locked while running), rebuilding, and relaunching it
  with the ROM — a one-time redeploy, not part of normal use.
