# Ralph Loop Task: Finish the HexManiacAdvance MCP server

You are working in the fork at **`Q:\Users\user\Projects\HexManiacAdvance-MCP`** (branch `mcp-integration`).
Use absolute paths; this fork is NOT the session's default project directory.

A working foundation already exists and is verified. Your job is to implement the
**4 remaining tool stubs** so the whole capability set works end to end.

## Definition of done (the ONLY exit condition)

Run the gate:

```bash
cd /q/Users/user/Projects/HexManiacAdvance-MCP
bash test/mcp-smoke.sh
```

It prints `PASS=N FAIL=M`. Output `<promise>FIXED</promise>` **only when it prints `ALL GREEN` and exits 0** — i.e. all 11 checks pass. Do not emit the promise on a partial pass, and never to escape the loop. If you are stuck, keep iterating; the promise must be literally true.

Currently green: build, tools/list (7 tools), open_rom, read_table.
Currently red (your work): write_value, save_rom, edit-persists, export encounters/trainers/dex, run_script.

## What to implement

Edit **`src/HexManiac.Mcp/RomTools.cs`**. Replace the four `NotImplementedException` stubs with real implementations. Keep the existing JSON-string return style and the `RomSession session` DI parameter. Do not change map/tilemap/sprite code — out of scope.

### Build & test commands

```bash
export PATH="/c/Program Files/dotnet:$PATH"          # dotnet SDK 6
dotnet build src/HexManiac.Mcp/HexManiac.Mcp.csproj -c Release   # must be 0 errors
bash test/mcp-smoke.sh                                # the gate
```

## API reference (HexManiac.Core) — already proven to work

```csharp
// session.Require() -> IDataModel (throws if no ROM loaded)
var model = session.Require();

// TABLE READ (already used by read_table)
ModelTable table = model.GetTableModel("data.pokemon.stats");   // null if name unknown
int rows = table.Count;
ModelArrayElement row = table[i];
int v   = row.GetValue("hp");            // integer field
string s = row.GetStringValue("name");   // PCS/text field
IReadOnlyList<ArrayRunElementSegment> segs = table.Run.ElementContent; // seg.Name, seg.Type

// EDIT  (write_value): use a ModelDelta token, then SetValue on the element.
var token = new HavenSoft.HexManiac.Core.Models.ModelDelta();
table[index].SetValue(field, value, token);   // overloads exist; confirm signature in ModelTable.cs
// (changes land in model.RawData)

// SAVE (save_rom):
System.IO.File.WriteAllBytes(outPath ?? session.RomPath!, model.RawData);

// SCRIPT (run_script): HavenSoft.HexManiac.Core.Models.Code.ScriptParser
//   var parser = new ScriptParser(model.GetShortGameCode()? , session.Singletons.ScriptLines, endToken);
//   Inspect ScriptParser.cs for the exact constructor + Parse/compile entry points.
//   A minimal "it ran without error" result is enough for the gate (id 13).
```

Useful standard anchors (constants on `HardcodeTablesModel`): `data.pokemon.stats`,
`data.trainers.stats`, `data.pokedex.stats`, `data.pokemon.wild`, `data.items.stats`.
Use the `list_tables` tool or `model.Anchors` to discover others.

### export_table
Read every row (like `read_table` but no paging) into a list and
`File.WriteAllText(outPath, JsonSerializer.Serialize(...))`. The gate exports
`data.pokemon.wild` (encounters), `data.trainers.stats`, and `data.pokedex.stats`.

## Gotchas (learned while building the foundation)

- **Paths:** the server runs on Windows .NET. Pass Windows paths (`Q:/...`), not MSYS (`/q/...`). The smoke test already converts via `cygpath -m`.
- **Resources:** `Singletons` loads `resources/` relative to the working dir; `Program.cs` pins it to the exe folder and the build copies `resources/` there. Don't break that.
- **Concurrency:** the server handles requests as they arrive; a client must await each response before the next call (the smoke test sleeps between phases). `open_rom` takes ~10–15s (model init).
- **stdout is sacred:** only JSON-RPC may go to stdout; logs go to stderr (already configured).
- After editing, ALWAYS rebuild before re-running the gate (the gate builds for you).

## Verify-before-promise checklist
1. `dotnet build ... -c Release` → 0 errors.
2. `bash test/mcp-smoke.sh` → `ALL GREEN`.
3. Only then: `<promise>FIXED</promise>`.

Full design rationale: `docs/superpowers/specs/2026-06-17-hexmaniac-mcp-design.md`.
