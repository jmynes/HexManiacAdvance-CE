# Building this fork

This fork contains two things with **different .NET toolchain needs**, so there
are two build commands. This is intentional.

## 1. The HexManiacAdvance GUI editor (upstream)

Targets `net6.0-windows` and builds with the **.NET 6 SDK**, exactly like
upstream. The root `global.json` pins SDK 6, and the build must go through the
**solution** (the WPF XAML build relies on `$(SolutionDir)` and build order).

```bash
# from the repo root
dotnet build -c Release -p:PlatformTarget=x64
```

Output: `artifacts/HexManiac.WPF/bin/Release/net6.0-windows/HexManiacAdvance.exe`

Launch it by running that exe (double-click or from a terminal). Edit the C#/XAML
under `src/HexManiac.WPF` or `src/HexManiac.Core`, rebuild, relaunch.

> Note: building a single WPF `.csproj` directly fails (`MC3066`), and building
> the solution under SDK 8 fails (`CS0102` in the WPF temp project). Use the
> command above (SDK 6 + solution). That's why the MCP project is **not** part of
> `HexManiacAdvance.sln`.

> Note: the Python scripting engine (`PythonTool`) is [pythonnet](https://github.com/pythonnet/pythonnet)
> embedding real CPython, not a system Python install. The first build of
> `HexManiac.WPF` or `HexManiac.Tests` downloads the official Windows embeddable
> CPython package (x64 + x86) into `artifacts/PythonRuntime/` (gitignored,
> shared across projects, cached after the first build) and stages it next to
> the output as `resources/python/{x64,x86}/`. This needs network access once;
> later builds use the cache. See `src/Directory.Build.targets`.

## 2. The MCP server (this fork's addition)

Targets `net8.0` and builds with the **.NET 8 SDK**. It has its own
`src/HexManiac.Mcp/global.json` pinning SDK 8, so build it **from its own
directory** (or let the smoke test do it):

```bash
# from the repo root
( cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release )

# verify the server end-to-end (builds + drives it over stdio):
bash test/mcp-smoke.sh        # expect: ALL GREEN
```

Output: `artifacts/HexManiac.Mcp/bin/Release/net8.0/HexManiac.Mcp.exe`
(this is the path `.mcp.json` points at). It's a stdio server — don't run it
directly; an MCP client (Claude Code) launches it. See `PROMPT.md` / the spec.

Both projects share the same `HexManiac.Core` (net6.0) library, which builds
fine under either SDK. All build output goes to one repo-root `artifacts/` dir
(a `SolutionDir` fallback in `src/Directory.Build.props` keeps single-project
and solution builds consistent).

## Live GUI mode

When a HexManiacAdvance GUI built from this fork is running, the MCP server
automatically targets the **ROMs open in that GUI** instead of loading from
disk:

- The GUI hosts a named-pipe automation server (`HexManiacAdvance.Automation`),
  started at launch.
- MCP tools (`list_open_roms`, `read_table`, `write_value`, `export_table`,
  `run_script`, `save_rom`) forward to the GUI's active tab and report
  `"mode": "live"`. Edits appear in the GUI immediately and are undoable there.
- Use `tab` (index) or `tabFile` (filename substring) to target a specific tab.
- When no GUI is running, the same tools fall back to **headless** mode
  (`open_rom` + load from disk), reporting `"mode": "headless"`.

### Reading & discovery

- `help` — show how to use this MCP. No `topic`: the full guide; `topic` (a tool name or
  section heading): just that section. Works live or headless. The same guide is also the
  `hexmaniac://guide` MCP resource.
- `list_tables` — list the named tables/anchors available in the loaded ROM.
- `read_table` — read a named table as JSON rows; `start`/`count` to page.
- `export_table` — export a named table to a JSON file (`outPath`).

### Navigation tools

- `list_shortcuts` — lists the GUI's "Goto" shortcut buttons as `{ display, anchor }`
  (e.g. `Pokemon`, `Trainers`, `Moves`, `Items`, `Maps`). Works in both live and
  headless mode.
- `goto` — **live only.** Navigates the active GUI tab to a target: a shortcut
  label (e.g. `Pokemon`), an anchor name (e.g. `data.pokemon.stats`), or a hex
  address. In headless mode it returns an error (there is no view to navigate).
  `bash test/mcp-live-smoke.sh` exercises `goto Pokemon` against the running GUI.

Verify the two modes:

```bash
bash test/mcp-smoke.sh        # headless gate -> ALL GREEN
bash test/mcp-live-smoke.sh   # live gate (launches the GUI) -> LIVE GREEN
```

### Typed write_value

`write_value` sets any field, not just integers. `value` is interpreted by the
field's type:

- text (name) and pointer-to-text (description, effect) ← a **string**
- integer (power, accuracy, pp) ← a **number**
- enum (type) ← the option **name** (e.g. `"FLYING"`) or a number index
- bit-array checkbox (e.g. a move's `target`) ← pass `flag="<name>"` with
  `value` `true`/`false`

Bad values return a clear error (e.g. an unknown enum value lists the valid
options). Works live (visible + undoable in the GUI) and headless.

- Bit-array checkbox flags are matched by friendly name, case-insensitive and
  ignoring the quotes HexManiac uses for names with spaces — e.g.
  `write_value(..., field="info", flag="Makes Contact", value=true)`.

### Editor operations

- `undo` / `redo` — apply up to `count` steps on the active tab's change history
  (same stack as Ctrl+Z/Y). Live + headless. Returns how many steps applied.
- `select` — select `count` rows from `index`, or the whole table if `index` is
  omitted; scrolls into view. **Live only.**
- `copy_rows` / `paste_rows` — copy `count` rows to a hex string (cached), then
  paste onto another index (undoable). Clones rows; live + headless.
- `clipboard_copy` / `clipboard_paste` — drive the GUI's real Copy/Paste over the
  current selection, sharing the system clipboard with manual Ctrl+C/V. **Live only.**

### ROM/tab lifecycle

- `open_rom` — live: opens the `.gba` as a new tab in the running GUI; headless:
  loads it as the single session ROM. Also reports `gameCode`/`recognized`/`metadataSource`.
  For a ROM that is NOT a recognized base game (FireRed/Emerald/...) and has no sidecar
  `.toml`, `metadata='auto'` (default) returns a `needsMetadataChoice` warning instead of
  opening — re-call with `metadata='find_toml'` (use a `.toml` next to the ROM),
  `metadata='guess'` (open with guessed offsets anyway), or `metadata='guess_offsets'`
  (auto-detect — not yet implemented).
- `duplicate_tab` — open a second tab on the resolved tab's ROM (like Ctrl+T),
  sharing its model and undo history. **Live only.** `close_rom` then closes all
  tabs of that ROM at once.
- `save_rom` — pass `outPath` to save a COPY, or `overwrite=true` to save over the
  loaded/open ROM. With neither it refuses (won't silently overwrite the source).
  **Auto-backup:** before overwriting an existing target file, `save_rom` snapshots
  the current `.gba`, its sidecar `.toml`, and its `.sav` into a timestamped
  `backups/` subdirectory next to the target (e.g. `backups/firered_20240615_123456.gba`).
  The result includes a `backedUp` list of the files written.
- `backup_rom` — explicitly create a backup of the loaded ROM (`.gba` + sidecar
  `.toml` + `.sav`) into a timestamped `backups/` subdirectory next to the ROM,
  without saving any edits. Returns the list of files written.
- `close_tab` — close one tab (default: active). **Live only.** Refuses on unsaved
  changes unless `force=true` (which discards them; no disk write, no dialog).
- `close_rom` — close ALL tabs showing the resolved tab's ROM. **Live only.** Same
  unsaved-change guard / `force` semantics.
- `launch_rom` — shell-opens the resolved ROM's on-disk file in your default GBA
  program (like the play button). The ROM must already be saved; pass `force=true`
  to launch the last-saved file even if there are unsaved edits. Returns
  `{ ok, launched, mode }` with the path that was launched. Works live and headless.

### ROM reference tools

- `supported_roms` — returns the list of supported Pokemon GBA ROMs with game codes,
  No-Intro names, and checksums. Call with no arguments for all 9 ROMs, or pass
  `code="BPRE0"` (any header code) to filter to a single entry. Does not require a
  ROM to be open.
- `identify_rom` — identifies the currently open ROM: reports `isCleanDump` (true if
  MD5/SHA1/CRC32 all match a known clean dump), `baseGame` (e.g. `"FireRed Rev 0 (v1.0)"`),
  and `headerCode`. Useful for detecting whether a ROM is an unmodified dump or a
  romhack of a known base.

The `hexmaniac://supported-roms` MCP resource exposes the raw JSON from
`src/HexManiac.Mcp/resources/supported-roms.json`. Human-readable table: see
[docs/SUPPORTED-ROMS.md](docs/SUPPORTED-ROMS.md).
