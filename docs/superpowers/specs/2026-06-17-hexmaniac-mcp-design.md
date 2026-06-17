# HexManiacAdvance MCP Server — Design Spec

**Date:** 2026-06-17
**Status:** Approved; foundation built and verified. Remaining tools to be implemented via Ralph loop.
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (fork of `haven1433/HexManiacAdvance` @ tag `v0.5.6.1`, branch `mcp-integration`)

## Goal

Add a headless **MCP (Model Context Protocol) server** to the HexManiacAdvance solution that lets an MCP client (Claude Code) read, query, export, and edit GBA Pokémon ROM data — and run HMA scripts — by reusing the existing `HexManiac.Core` library. Scope for v1: **everything except map editing.**

## Why this shape

`HexManiac.Core` already encodes what GBA Pokémon data *means* (tables/anchors, field formats, text encoding, scripts). Re-implementing that elsewhere is infeasible, so the MCP server is a thin C# host **inside the solution** that references `HexManiac.Core` directly. This is the deepest, lowest-risk integration.

## Architecture

```
MCP client (Claude Code)
        │  stdio (newline-delimited JSON-RPC)
        ▼
HexManiac.Mcp  (net8.0 console app)        ← new project
   • Program.cs      host + stdio transport + tool discovery
   • RomSession.cs   DI singleton: holds the loaded IDataModel
   • RomTools.cs     [McpServerToolType] — the tool methods
        │  direct project reference
        ▼
HexManiac.Core (net6.0)  — unchanged
   • HardcodeTablesModel / PokemonModel / IDataModel
   • ModelTable / ModelArrayElement (table read/edit)
   • ScriptParser (HMA scripting)
   • Singletons (loads resources/ reference files)
```

### Components & contracts

- **`RomSession`** (singleton, injected into tools): owns `Singletons` and the loaded `IDataModel`. `Load(path)` constructs `new HardcodeTablesModel(singletons, File.ReadAllBytes(path), new StoredMetadata(new string[0]))` and waits on `InitializationWorkload`. `Require()` returns the model or throws a clear "no ROM loaded" error.
- **`RomTools`** (`[McpServerToolType]`): one method per tool, each decorated `[McpServerTool]` + `[Description]`. The `RomSession` parameter is supplied by DI; remaining parameters are tool arguments. Each tool returns a JSON string.
- **`Program`**: pins working directory to `AppContext.BaseDirectory` (so `resources/` resolves), clears stdout logging providers (stdout is reserved for the protocol; logs go to stderr), wires `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()`.

### Build / toolchain decisions

- `global.json` bumped from SDK 6.0 → **8.0** (`rollForward: latestMinor`). The MCP SDK's transitive dependencies do not support net6.0 at runtime; net8.0 is fully supported and its runtime is installed. `HexManiac.Core` still targets **net6.0** and builds cleanly under SDK 8 (verified, 0 errors). A net8.0 project references the net6.0 library without issue.
- Package: **`ModelContextProtocol` 1.4.0** (official C# SDK) + `Microsoft.Extensions.Hosting` 8.0.1.
- **`resources/`**: `Singletons` reads reference files (`scriptReference.txt`, `hma.py`, `*.toml`, …) from a `resources/` folder relative to the working directory, and some reads are unguarded (a missing folder hard-throws). The 0.5.6.1 `resources/` folder (32 entries) is vendored into `src/HexManiac.Mcp/resources/` and copied next to the exe on build.

## Tools (v1)

| Tool | Status | Contract |
|------|--------|----------|
| `open_rom(path)` | ✅ done | Load a `.gba`. Returns `{ok, path, length, anchorCount}`. Required before others. |
| `list_tables(filter?)` | ✅ done | Returns `{count, tables[]}` — anchor names, optional substring filter. |
| `read_table(name, start?, count?)` | ✅ done | Returns `{name, total, start, returned, fields[], rows[]}`. Paged. |
| `write_value(table, index, field, value)` | ⛔ TODO | `new ModelDelta()`; `element.SetValue(field, value, token)`. Return old/new value. |
| `export_table(name, outPath)` | ⛔ TODO | Full table (no paging) → JSON file on disk. Used for encounters/trainers/dex. |
| `run_script(script)` | ⛔ TODO | Parse/run an HMA script via `ScriptParser` + `Singletons.ScriptLines`. Return output. |
| `save_rom(outPath?)` | ⛔ TODO | `File.WriteAllBytes(outPath ?? RomPath, model.RawData)`. |

Standard anchors are constants on `HardcodeTablesModel` (e.g. `data.pokemon.stats`, `data.trainers.stats`, `data.pokedex.stats`, `data.pokemon.wild`, `data.items.stats`).

## Verification (definition of done)

A deterministic stdio smoke-test (`test/mcp-smoke.sh`) is the source of truth — it launches the built server and drives JSON-RPC, asserting expected results. This is preferred over Claude's live MCP client because it needs no session restart and runs unattended each loop iteration.

The loop may emit `<promise>FIXED</promise>` **only when all six pass**:

1. `dotnet build` of the solution succeeds (0 errors).
2. Server responds to `initialize` + `tools/list` (lists all 7 tools).
3. `open_rom` on test FireRed → `anchorCount > 0`; `read_table data.pokemon.stats` index 1 returns Bulbasaur's known stats (hp 45, attack 49).
4. `write_value` a stat → `save_rom` to a temp copy → re-`open_rom` that copy → `read_table` reflects the change.
5. `export_table` writes valid JSON files for **encounters, trainers, and dex**.
6. `run_script` executes one HMA script and returns non-error output.

Steps 1–3 already pass with the committed skeleton.

## Out of scope (v1)

Map/tilemap/blockset editing; sprite/palette editing; UI; multi-ROM sessions; undo/redo exposure.

## Test assets

Clean base FireRed at `test/roms/firered.gba` (git-ignored). Known value anchor for assertions: `data.pokemon.stats[1]` = Bulbasaur (hp 45, attack 49, def 49, speed 45, spatk 65, spdef 65).
