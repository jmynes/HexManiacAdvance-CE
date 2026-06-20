# MCP Documentation + Runtime Help Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or executing-plans). Checkbox (`- [ ]`) steps.

**Goal:** Add a hand-written `docs/MCP.md` user guide, embed it in the MCP assembly, and surface it at runtime via a `help(topic?)` tool and MCP resources.

**Architecture:** `docs/MCP.md` is the single source; it is embedded into `HexManiac.Mcp` (csproj `EmbeddedResource`) and loaded by a small `Docs` helper (load + split by markdown heading). The `help` tool and the MCP resource both serve that embedded text. MCP project only.

**Tech Stack:** C# / .NET (MCP net8.0), ModelContextProtocol 1.4.0, `System.Reflection` (manifest resource), bash + jq gate.

## Global Constraints

- Headless `test/mcp-smoke.sh` stays ALL GREEN. Live gate unaffected.
- `help` works in both modes (reads the embedded doc); result stamped `mode` via the existing wrapper. Tool count 20 → 21.
- No Core/WPF changes.
- Build: MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; GUI `dotnet build -c Release -p:PlatformTarget=x64`. Headless gate with NO GUI.
- Authoritative current tool list (20) the guide must cover: `open_rom`, `save_rom`, `list_open_roms`, `list_tables`, `read_table`, `export_table`, `write_value`, `run_script`, `list_shortcuts`, `goto`, `undo`, `redo`, `select`, `copy_rows`, `paste_rows`, `clipboard_copy`, `clipboard_paste`, `close_tab`, `close_rom`, `duplicate_tab` (+ `help` once added = 21). Source the exact one-line descriptions from each tool's `[Description]` in `src/HexManiac.Mcp/RomTools.cs` and the notes in `BUILD.md`.

---

## File Structure

- **Create `docs/MCP.md`** — the user guide (also embedded).
- **Modify `src/HexManiac.Mcp/HexManiac.Mcp.csproj`** — embed `docs/MCP.md` as `HexManiac.Mcp.MCP.md`.
- **Create `src/HexManiac.Mcp/Docs.cs`** — load embedded markdown + section split.
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — add the `help` tool.
- **Create `src/HexManiac.Mcp/HelpResources.cs`** — MCP resource(s) for the guide/tool index.
- **Modify `src/HexManiac.Mcp/Program.cs`** — register resources if needed.
- **Modify `test/mcp-smoke.sh`** — help + resources assertions; tool count → 21.
- **Modify `README.md`** — link to `docs/MCP.md`.

---

## Task 1: `docs/MCP.md` + embed + `help` tool

**Files:** Create `docs/MCP.md`; modify `HexManiac.Mcp.csproj`; create `Docs.cs`; modify `RomTools.cs`, `test/mcp-smoke.sh`, `README.md`.

**Interfaces:**
- Produces: `Docs.Guide()` → full embedded markdown string; `Docs.Section(string topic)` → matching section text or a "sections: …" fallback. MCP tool `help(topic = null)`.

- [ ] **Step 1: Write the failing test — extend `test/mcp-smoke.sh`.** Bump tool count `20`→`21`. Add driver calls after the last line (inside the `{…} | "./$EXE"` block):
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":45,"method":"tools/call","params":{"name":"help","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":46,"method":"tools/call","params":{"name":"help","arguments":{"topic":"write_value"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":47,"method":"tools/call","params":{"name":"help","arguments":{"topic":"nonsense_zzz"}}}'; sleep 1
```
Assertions:
```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "21" ] && ok "tools/list shows 21 tools" || bad "tools/list"
[ "$(result_text 45 2>/dev/null | grep -ci 'open_rom')" -ge 1 ] && ok "help overview mentions open_rom" || bad "help overview"
[ "$(result_text 46 2>/dev/null | grep -ci 'write_value')" -ge 1 ] && ok "help topic returns section" || bad "help topic"
[ "$(result_text 47 2>/dev/null | grep -ci 'section')" -ge 1 ] && ok "help unknown topic lists sections" || bad "help unknown topic"
```
(`result_text` already extracts `result.content[0].text`.)

- [ ] **Step 2: Run the gate to verify it fails.** `bash test/mcp-smoke.sh` (no GUI) — Expected: FAIL (`help` unknown tool; count 20≠21).

- [ ] **Step 3: Write `docs/MCP.md`.** A complete user guide with these `##` sections (each a real heading so `help(topic)` can match):
  - `# HexManiacAdvance MCP` — one-paragraph what-it-is.
  - `## Quick start` — add to `.mcp.json` (the server exe is `artifacts/HexManiac.Mcp/bin/Release/net8.0/HexManiac.Mcp.exe`); reconnect with `/mcp` after a rebuild.
  - `## Live vs headless` — if the HexManiacAdvance GUI is running, tools target its open tabs (`mode:"live"`, visible + undoable); else a single headless ROM (`mode:"headless"`). Every result has a `mode` field.
  - `## Open a ROM first` — `open_rom` (headless loads one ROM; live opens a new GUI tab). Recognized games (FireRed/Emerald/…) vs the metadata warning for others.
  - `## Reading data` — `list_tables`, `read_table`, `export_table` with examples.
  - `## Editing values (write_value)` — value types: string (text/enum name), number (integer / enum index), `true/false` + `flag="<checkbox>"` (friendly names, quotes optional); examples for name/power/type/checkbox.
  - `## Navigation` — `goto`, `list_shortcuts`.
  - `## Undo / redo, select, copy/paste` — `undo`, `redo`, `select`, `copy_rows`, `paste_rows`, `clipboard_copy`, `clipboard_paste`.
  - `## ROM/tab lifecycle` — `open_rom`, `save_rom` (needs `outPath` or `overwrite=true`), `close_tab`, `close_rom`, `duplicate_tab`, `list_open_roms`.
  - `## Scripts` — `run_script`.
  - `## Metadata & unrecognized ROMs` — the `needsMetadataChoice` warning + `find_toml`/`guess_offsets`/`guess`.
  - `## Saving & backups safety` — `save_rom` guard.
  - `## Recipes` — e.g. "rename a move", "make a move Fire-type 120 power", "clone a row", "open + edit a second ROM".
  - `## Tools index` — every tool name + its one-line description (copy each tool's `[Description]` text from `RomTools.cs`).
  - `## Troubleshooting` — reconnect after rebuild; "needs metadata choice"; live vs headless confusion.
  Keep examples as MCP tool-call argument JSON. Be accurate to the 20 tools listed in Global Constraints.

- [ ] **Step 4: Embed `docs/MCP.md`.** In `src/HexManiac.Mcp/HexManiac.Mcp.csproj`, inside an `<ItemGroup>`:
```xml
    <EmbeddedResource Include="..\..\docs\MCP.md" Link="MCP.md" LogicalName="HexManiac.Mcp.MCP.md" />
```
(Confirm the relative path from the csproj to `docs/MCP.md`; adjust `..\..` as needed.)

- [ ] **Step 5: Create `src/HexManiac.Mcp/Docs.cs`.**
```csharp
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HexManiac.Mcp;

public static class Docs {
   private static string _cache;
   public static string Guide() {
      if (_cache != null) return _cache;
      var asm = Assembly.GetExecutingAssembly();
      var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("MCP.md", StringComparison.OrdinalIgnoreCase));
      if (name == null) return _cache = "MCP guide resource not found (build misconfiguration).";
      using var s = asm.GetManifestResourceStream(name)!;
      using var r = new StreamReader(s);
      return _cache = r.ReadToEnd();
   }
   // Return the section whose ## / ### heading contains `topic` (case-insensitive),
   // through to the next same-or-higher heading; else a "sections:" listing.
   public static string Section(string topic) {
      var guide = Guide();
      var lines = guide.Replace("\r\n", "\n").Split('\n');
      var headings = lines.Where(l => l.StartsWith("## ")).Select(l => l.TrimStart('#', ' ')).ToList();
      int start = -1;
      for (int i = 0; i < lines.Length; i++) {
         if (lines[i].StartsWith("## ") && lines[i].ToLowerInvariant().Contains(topic.ToLowerInvariant())) { start = i; break; }
      }
      if (start < 0) return $"No section matches '{topic}'. Sections: {string.Join(", ", headings)}. Call help with no topic for the full guide.";
      int end = lines.Length;
      for (int i = start + 1; i < lines.Length; i++) { if (lines[i].StartsWith("## ")) { end = i; break; } }
      return string.Join("\n", lines.Skip(start).Take(end - start));
   }
}
```

- [ ] **Step 6: Add the `help` tool to `RomTools.cs`.**
```csharp
   [McpServerTool(Name = "help")]
   [Description("Show how to use this MCP. No topic: the full guide. topic: a tool name or section heading (e.g. 'write_value', 'lifecycle') returns that section. Works live or headless.")]
   public string Help([Description("Optional tool name or section heading")] string? topic = null) {
      var text = string.IsNullOrWhiteSpace(topic) ? Docs.Guide() : Docs.Section(topic);
      return Stamp(JsonSerializer.Serialize(new { ok = true, text }, Json), GuiBridge.IsGuiRunning() ? "live" : "headless");
   }
```
(Note: `help` has no `RomSession`/ROM dependency — that's fine; the SDK injects only what the method declares. `Stamp`/`Json`/`GuiBridge` are in scope.)

Wait — the gate asserts `result_text` = `result.content[0].text` is the raw markdown containing "open_rom". But `Help` returns a JSON object `{ok,text,mode}`, so `result.content[0].text` is that JSON string, and `grep -ci open_rom` on it still matches (the markdown is inside `text`). The assertions use `grep -ci`, not `jq`, so they pass. Keep the `{ok,text,mode}` shape for consistency with other tools.

- [ ] **Step 7: Link from `README.md`.** Add near the top: `> **Using the MCP server?** See [docs/MCP.md](docs/MCP.md) for setup and the tool guide (or call the \`help\` tool).`

- [ ] **Step 8: Build MCP + GUI.** Both → 0 errors. (Embedding a file outside the project dir is supported; if the build can't find `..\..\docs\MCP.md`, fix the relative path.)

- [ ] **Step 9: Run the gate.** `bash test/mcp-smoke.sh` (no GUI) → `ALL GREEN` incl. the 4 new assertions; prior assertions pass.

- [ ] **Step 10: Commit.**
```bash
git add docs/MCP.md src/HexManiac.Mcp/HexManiac.Mcp.csproj src/HexManiac.Mcp/Docs.cs src/HexManiac.Mcp/RomTools.cs test/mcp-smoke.sh README.md
git commit -m "MCP docs: docs/MCP.md (embedded) + help(topic) tool"
```

---

## Task 2: MCP resources for the guide

**Files:** Create `src/HexManiac.Mcp/HelpResources.cs`; modify `src/HexManiac.Mcp/Program.cs` (register if needed); modify `test/mcp-smoke.sh`.

**Interfaces:**
- Consumes: `Docs.Guide()` (Task 1).
- Produces: an MCP resource `hexmaniac://guide` (the full guide, `text/markdown`).

- [ ] **Step 1: Verify the ModelContextProtocol 1.4.0 resource API.** Inspect the package (the `ModelContextProtocol` assembly / its `McpServerResource*` types and the `IMcpServerBuilder` resource-registration extension, e.g. `WithResourcesFromAssembly()` / `WithResources<T>()`). Grep the SDK for `McpServerResource`, `ResourceType`, `WithResources`. Write the exact attribute + registration shape into a short note in the report. **If the SDK does not support server resources in 1.4.0, STOP Task 2, report it, and leave the `help` tool as the delivered runtime-docs path (do not fake a resource).**

- [ ] **Step 2: Write the failing test — extend `test/mcp-smoke.sh`.** Add (drive the resource endpoints; adjust ids):
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":48,"method":"resources/list","params":{}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":49,"method":"resources/read","params":{"uri":"hexmaniac://guide"}}'; sleep 1
```
Assertions:
```bash
[ "$(jq -rs 'map(select(.id==48))[0].result.resources | map(.uri) | join(" ")' "$OUT" 2>/dev/null | grep -ci 'hexmaniac://guide')" -ge 1 ] && ok "resources/list has guide" || bad "resources/list"
[ "$(jq -rs 'map(select(.id==49))[0].result.contents[0].text // empty' "$OUT" 2>/dev/null | grep -ci 'open_rom')" -ge 1 ] && ok "resources/read guide non-empty" || bad "resources/read"
```
(Adjust the JSON paths to the actual `resources/*` response shape found in Step 1 — the SDK's `contents`/`text` field names. If the shape differs, fix the assertions to match what the server actually returns.)

- [ ] **Step 3: Run the gate to verify it fails.** Expected: FAIL — `resources/list` returns empty / no `hexmaniac://guide`.

- [ ] **Step 4: Implement the resource.** Create `HelpResources.cs` with the `[McpServerResourceType]`/`[McpServerResource(UriTemplate="hexmaniac://guide", Name="HexManiacAdvance MCP guide", MimeType="text/markdown")]` method returning `Docs.Guide()` (exact API per Step 1). Register via the builder in `Program.cs` if `WithToolsFromAssembly` doesn't also pick up resources (add `.WithResourcesFromAssembly()` or equivalent).

- [ ] **Step 5: Build MCP.** → 0 errors.

- [ ] **Step 6: Run the gate.** `bash test/mcp-smoke.sh` (no GUI) → `ALL GREEN` incl. the resource assertions; all prior pass.

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/HelpResources.cs src/HexManiac.Mcp/Program.cs test/mcp-smoke.sh
git commit -m "MCP resources: expose the guide at hexmaniac://guide"
```

---

## Self-Review notes
- **Spec coverage:** docs/MCP.md (T1 Step 3); embed (T1 Step 4); Docs helper (T1 Step 5); help(topic) tool incl. no-topic/section/unknown (T1 Step 6 + assertions); README link (T1 Step 7); MCP resources with API-verification gate (T2). Covered.
- **Headless invariant:** gate run T1 Step 9 + T2 Step 6; tool count 20→21.
- **Type consistency:** `Docs.Guide()`/`Docs.Section(topic)` defined T1, consumed by `help` (T1) and the resource (T2). `help` returns `{ok,text,mode}`; `grep`-based assertions match raw markdown inside `text`. Resource API confirmed in T2 Step 1 before use.
- **Risk flagged:** if MCP 1.4.0 lacks server resources, T2 stops and reports; the `help` tool still satisfies the runtime-docs goal.
