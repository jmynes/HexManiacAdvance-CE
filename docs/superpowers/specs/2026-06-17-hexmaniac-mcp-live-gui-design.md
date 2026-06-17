# Live GUI ⇄ MCP Integration — Design Spec

**Date:** 2026-06-17
**Status:** Approved (design). Builds on the existing headless MCP server.
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (HexManiacAdvance @ 0.5.6.1, branch `mcp-integration`)

## Goal

Let the MCP server read and edit the ROMs **currently open in a running
HexManiacAdvance GUI** — including unsaved in-memory edits — with MCP edits
appearing live in the GUI (and undoable there). **Headless operation must remain
fully available** when no GUI is running.

## Non-negotiable constraint: headless still works

The existing headless behavior (server loads a `.gba` from disk via `open_rom`)
must keep working unchanged when the GUI is closed. The live path is purely
additive: a second backend that activates only when a GUI is detected. The
headless smoke gate (`test/mcp-smoke.sh`) stays green and unchanged.

## Architecture

One MCP server, two backends, chosen per-call at runtime:

```
Claude --stdio--> HexManiac.Mcp.exe --+-- GUI pipe reachable? --> pipe client --> HexManiacAdvance.exe
  (.mcp.json unchanged)               |                                            EditorViewModel tabs
                                      +-- not reachable        --> headless RomSession (loads .gba)
```

- **Live mode:** the GUI's automation pipe is reachable → operate on its open tabs.
- **Headless mode:** pipe not reachable → load/operate on a disk ROM (today's behavior).
- Every tool result carries a `mode: "live" | "headless"` field so the caller
  always knows which backend answered.

## Components

1. **`src/HexManiac.Core/RomAutomation.cs`** (new; in Core so both sides share it).
   Pure functions over `IDataModel` / `ViewPort`:
   - `ReadTable(model, name, start, count) -> TableDto`
   - `WriteValue(model, tokenFactory, table, index, field, value) -> WriteDto`
   - `ExportTable(model, name) -> TableDto` (all rows)
   - `RunScript(viewPort, scriptText|path) -> ScriptDto`
   - `ListTables(model, filter) -> string[]`
   This is the single implementation of the table logic; both backends call it
   (removes the duplication that would otherwise exist, and the current
   `RomTools` read/write logic moves here).

2. **`src/HexManiac.WPF/AutomationPipeServer.cs`** (new). Owns a
   `NamedPipeServerStream` named `HexManiacAdvance.Automation`, started at app
   launch (always on for v1). Reads length-prefixed JSON requests
   `{ method, params }`, **marshals each onto the WPF UI thread via
   `Application.Current.Dispatcher.Invoke`**, runs it against `EditorViewModel`'s
   tabs through `RomAutomation`, and writes a JSON `{ result | error }`.
   Persistent connection; one request/response at a time.

3. **`src/HexManiac.Mcp/GuiBridge.cs`** (new). A pipe *client*. `TryConnect()`
   attempts the named pipe with a short timeout. When connected, the MCP tools
   forward their call as a pipe request and return its result; otherwise they use
   the headless `RomSession`.

## Pipe protocol

Localhost, same-user, no auth (v1). Length-prefixed UTF-8 JSON, request/response:

```
request : { "method": "list_tabs|read_table|write_value|export_table|run_script|save_rom",
            "params": { ... } }
response: { "ok": true,  "result": { ... } }
        | { "ok": false, "error": "message" }
```

`list_tabs -> [{ index, file, selected }]`. The other methods mirror the MCP
tools and include a `tab` selector (index or filename; default = selected tab).

## Tool surface (MCP)

- **New:** `list_open_roms` → live: the GUI's tabs; headless: the one loaded ROM
  (or empty).
- **Changed:** `read_table`, `write_value`, `export_table`, `run_script`,
  `save_rom` gain an optional `tab` arg (index or filename; defaults to the GUI's
  active tab). Ignored in headless mode.
- **Unchanged:** `open_rom` / `list_tables` — `open_rom` is a headless-only
  convenience (in live mode the ROMs are already open).

## Safety & threading

- All GUI model access runs on the Dispatcher thread; the pipe server marshals
  each request there before touching a tab.
- Live **writes go through the tab's `CurrentChange` token**, so they enter the
  GUI's undo history and render immediately.
- Single automation pipe with a fixed name: if two GUI instances run, the first
  to bind owns it (documented v1 limitation).
- Reads/writes that target a closed/headless session return a clear error, never
  a crash.

## Testing

- **Headless gate** (`test/mcp-smoke.sh`): unchanged, must stay ALL GREEN.
- **Live gate** (`test/mcp-live-smoke.sh`, new): launch the built GUI with the
  test ROM, then drive the MCP server's live tools — assert `list_open_roms`
  shows the ROM, a known value reads correctly, a write reads back, and every
  response reports `mode == "live"`. Kill the GUI at the end.
- **Manual check:** an MCP `write_value` is visible in the GUI window (and
  undoable).

## Out of scope (v1)

Map/sprite/tilemap editing; multiple simultaneous GUI instances; remote / non-
localhost transport; pipe authentication; auto-launching the GUI from the MCP.

## Build impact

`RomAutomation` lives in Core (net6.0) so both the WPF GUI (net6.0) and the MCP
server (net8.0) can reference it. No change to the two-SDK build split documented
in `BUILD.md`.
