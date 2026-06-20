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
