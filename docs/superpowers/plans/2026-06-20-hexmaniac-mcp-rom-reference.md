# MCP ROM Reference + Identify Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Checkbox (`- [ ]`) steps.

**Goal:** A repo-tracked, MCP-embedded supported-ROM reference (`supported-roms.json`) exposed via a `supported_roms` tool + `hexmaniac://supported-roms` resource, plus an `identify_rom` tool that reports whether the loaded ROM is a clean dump and which base game/revision it is (incl. romhack base detection), plus a tracked human doc.

**Architecture:** `supported-roms.json` lives in `src/HexManiac.Mcp/resources/` (git-tracked) and is embedded into the MCP assembly. A `SupportedRoms` loader (mirrors `Docs`) reads/parses it. Tools and the resource read from it. `identify_rom` hashes the loaded ROM's `RawData`, reads its header game code via `IDataModel.GetGameCode()`, and matches the reference. No Core changes.

**Tech Stack:** C# / .NET (MCP net8.0, WPF net6.0), System.Text.Json, System.Security.Cryptography (MD5/SHA1), Force.Crc32 (already referenced by Core), bash + jq gate.

## Global Constraints
- Headless `test/mcp-smoke.sh` stays ALL GREEN. Tests use the throwaway `$OR` copy; source ROM md5 unchanged (`Pokemon - FireRed Version (USA).gba` = `e26ee0d44e809351c8ce2d73c7400cdd`).
- Reuse `Dispatch`/`Stamp`, `RomAutomation.Err`, `ResolveTab`/`NoTab`/`Ok`, `GuiBridge.IsGuiRunning()`. Namespace is `HavenSoft.HexManiac.Mcp`.
- `IDataModel.GetGameCode()` (extension in `HavenSoft.HexManiac.Core`) returns e.g. `"BPRE0"`. `Force.Crc32.Crc32Algorithm.Compute(byte[])` returns a uint (format `X8`).
- Build: MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; GUI `dotnet build -c Release -p:PlatformTarget=x64`. Headless gate with NO GUI.
- Tool count today is 23; this plan adds 2 (`supported_roms`, `identify_rom`) → 25.

## File Structure
- Create `src/HexManiac.Mcp/resources/supported-roms.json` (tracked data).
- Modify `src/HexManiac.Mcp/HexManiac.Mcp.csproj` (embed it).
- Create `src/HexManiac.Mcp/SupportedRoms.cs` (loader/lookup).
- Modify `src/HexManiac.Mcp/RomTools.cs` (`supported_roms` + `identify_rom` tools).
- Modify `src/HexManiac.Mcp/AutomationPipeServer.cs` (`identify_rom` live case).
- Modify `src/HexManiac.Mcp/HelpResources.cs` (`hexmaniac://supported-roms`).
- Create `docs/SUPPORTED-ROMS.md`; modify `README.md`, `BUILD.md`, `docs/MCP.md`.
- Modify `test/mcp-smoke.sh`.

---

## Task 1: data + loader + `supported_roms` tool + resource

**Files:** create `supported-roms.json`, `SupportedRoms.cs`; modify csproj, `RomTools.cs`, `HelpResources.cs`, `test/mcp-smoke.sh`.

**Interfaces:**
- Produces: `SupportedRoms.Json()` → embedded JSON text; `SupportedRoms.Lookup(code)` → filtered text. MCP tool `supported_roms(code=null)`; resource `hexmaniac://supported-roms`.

- [ ] **Step 1: Write the failing test (`test/mcp-smoke.sh`).** Bump tool count `23`→`24`. Add after the last driver line:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":56,"method":"tools/call","params":{"name":"supported_roms","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":57,"method":"tools/call","params":{"name":"supported_roms","arguments":{"code":"BPEE0"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":58,"method":"resources/list","params":{}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":59,"method":"resources/read","params":{"uri":"hexmaniac://supported-roms"}}'; sleep 1
```
Assertions:
```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "24" ] && ok "tools/list shows 24 tools" || bad "tools/list"
[ "$(result_text 56 | grep -ci 'BPRE0')" -ge 1 ] && ok "supported_roms lists BPRE0" || bad "supported_roms all"
[ "$(result_text 57 | grep -ci 'Emerald')" -ge 1 ] && ok "supported_roms code filter" || bad "supported_roms code"
[ "$(jq -rs 'map(select(.id==58))[0].result.resources|map(.uri)|join(" ")' "$OUT" 2>/dev/null | grep -ci 'hexmaniac://supported-roms')" -ge 1 ] && ok "resources/list has supported-roms" || bad "resources/list roms"
[ "$(jq -rs 'map(select(.id==59))[0].result.contents[0].text // empty' "$OUT" 2>/dev/null | grep -ci 'BPRE0')" -ge 1 ] && ok "resources/read supported-roms" || bad "resources/read roms"
```

- [ ] **Step 2: Run the gate → RED** (`supported_roms` unknown; count 23≠24).

- [ ] **Step 3: Create `src/HexManiac.Mcp/resources/supported-roms.json`** (verbatim):
```json
{
  "description": "Pokemon GBA base games and HexManiacAdvance support levels.",
  "recognition": "HMA identifies games by the 4-char header game code + version byte (e.g. BPRE0), not by hash. A romhack of a supported base keeps that code and stays supported. CRC32/SHA1 match HMA's hash tool format (uppercase).",
  "supportTiers": {
    "prime": "Primary, best-supported targets (FireRed v1.0 BPRE0, Emerald BPEE0).",
    "full": "Complete reference tables in HMA (tableReference.txt / HardcodeTablesModel).",
    "reduced": "Default metadata only (reduced features).",
    "none": "No game-aware support; opens as a plain hex editor."
  },
  "roms": [
    { "game": "FireRed", "revision": "Rev 0 (v1.0)", "headerCode": "BPRE0", "noIntroName": "Pokemon - FireRed Version (USA).gba", "md5": "e26ee0d44e809351c8ce2d73c7400cdd", "sha1": "41CB23D8DCCC8EBD7C649CD8FBB58EEACE6E2FDC", "crc32": "DD88761C", "hmaSupport": "prime" },
    { "game": "FireRed", "revision": "Rev 1 (v1.1)", "headerCode": "BPRE1", "noIntroName": "Pokemon - FireRed Version (USA, Europe) (Rev 1).gba", "md5": "51901a6e40661b3914aa333c802e24e8", "sha1": "DD5945DB9B930750CB39D00C84DA8571FEEBF417", "crc32": "84EE4776", "hmaSupport": "full" },
    { "game": "Emerald", "revision": "Rev 0", "headerCode": "BPEE0", "noIntroName": "Pokemon - Emerald Version (USA, Europe).gba", "md5": "605b89b67018abcea91e693a4dd25be3", "sha1": "F3AE088181BF583E55DAF962A92BB46F4F1D07B7", "crc32": "1F1C08FB", "hmaSupport": "prime" },
    { "game": "Ruby", "revision": "Rev 0 (v1.0)", "headerCode": "AXVE0", "noIntroName": "Pokemon - Ruby Version (USA, Europe).gba", "md5": "53d1a2027ab49df34a689faa1fb14726", "sha1": "F28B6FFC97847E94A6C21A63CACF633EE5C8DF1E", "crc32": "F0815EE7", "hmaSupport": "full" },
    { "game": "Ruby", "revision": "Rev 1 (v1.1)", "headerCode": "AXVE1", "noIntroName": "Pokemon - Ruby Version (USA, Europe) (Rev 1).gba", "md5": "e0503182a2e699678bcf25a6897a24d6", "sha1": "610B96A9C9A7D03D2BAFB655E7560CCFF1A6D894", "crc32": "61641576", "hmaSupport": "full" },
    { "game": "Sapphire", "revision": "Rev 0 (v1.0)", "headerCode": "AXPE0", "noIntroName": "Pokemon - Sapphire Version (USA, Europe).gba", "md5": "f34e91399c719812e66e2c828a2e93d7", "sha1": "3CCBBD45F8553C36463F13B938E833F652B793E4", "crc32": "554DEDC4", "hmaSupport": "full" },
    { "game": "Sapphire", "revision": "Rev 1 (v1.1)", "headerCode": "AXPE1", "noIntroName": "Pokemon - Sapphire Version (USA, Europe) (Rev 1).gba", "md5": "3a32fd98b065283d09eeba1ce0542888", "sha1": "4722EFB8CD45772CA32555B98FD3B9719F8E60A9", "crc32": "BAFEDAE5", "hmaSupport": "full" },
    { "game": "LeafGreen", "revision": "Rev 0 (v1.0)", "headerCode": "BPGE0", "noIntroName": "Pokemon - LeafGreen Version (USA).gba", "md5": "612ca9473451fa42b51d1711031ed5f6", "sha1": "574FA542FFEBB14BE69902D1D36F1EC0A4AFD71E", "crc32": "D69C96CC", "hmaSupport": "full" },
    { "game": "LeafGreen", "revision": "Rev 1 (v1.1)", "headerCode": "BPGE1", "noIntroName": "Pokemon - LeafGreen Version (USA, Europe) (Rev 1).gba", "md5": "9d33a02159e018d09073e700e1fd10fd", "sha1": "7862C67BDECBE21D1D69CE082CE34327E1C6ED5E", "crc32": "DAFFECEC", "hmaSupport": "full" }
  ],
  "alsoSupported": [],
  "notSupported": [
    { "headerCode": "AXVE2", "game": "Ruby Rev 2", "hmaSupport": "none" },
    { "headerCode": "AXPE2", "game": "Sapphire Rev 2", "hmaSupport": "none" },
    { "headerCode": "BPEF0/BPEI0/BPRF0/...", "game": "Non-English R/S/E/FR/LG language variants", "hmaSupport": "reduced" }
  ]
}
```

- [ ] **Step 4: Embed it** — in `HexManiac.Mcp.csproj`, add inside an `<ItemGroup>`:
```xml
    <EmbeddedResource Include="resources\supported-roms.json" LogicalName="HexManiac.Mcp.supported-roms.json" />
```
(Confirm the path is relative to the csproj; `resources\` is the right relative dir.)

- [ ] **Step 5: Create `src/HexManiac.Mcp/SupportedRoms.cs`** (mirror Docs.cs; use the real namespace `HavenSoft.HexManiac.Mcp`):
```csharp
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace HavenSoft.HexManiac.Mcp {
   public static class SupportedRoms {
      private static string _cache;
      public static string Json() {
         if (_cache != null) return _cache;
         var asm = Assembly.GetExecutingAssembly();
         var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("supported-roms.json", StringComparison.OrdinalIgnoreCase));
         if (name == null) return _cache = "{\"error\":\"supported-roms.json resource not found (build misconfiguration).\"}";
         using var s = asm.GetManifestResourceStream(name)!;
         using var r = new StreamReader(s);
         return _cache = r.ReadToEnd();
      }
      // Return the rom entries whose headerCode contains `code` (case-insensitive), as a JSON array string.
      public static string Lookup(string code) {
         using var doc = JsonDocument.Parse(Json());
         if (!doc.RootElement.TryGetProperty("roms", out var roms)) return "[]";
         var matches = roms.EnumerateArray()
            .Where(e => e.TryGetProperty("headerCode", out var c) && c.GetString() is string s
                        && s.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(e => e.GetRawText());
         return "[" + string.Join(",", matches) + "]";
      }
   }
}
```

- [ ] **Step 6: Add the `supported_roms` tool (`RomTools.cs`).**
```csharp
   [McpServerTool(Name = "supported_roms")]
   [Description("Reference of the Pokemon GBA base games HexManiacAdvance supports: header codes, No-Intro names, md5/sha1/crc32, and support tier. No code: the whole reference. code (e.g. 'BPRE0' or 'bpre'): matching entries.")]
   public string SupportedRoms_([Description("Optional header code filter, e.g. BPRE0")] string? code = null) {
      var text = string.IsNullOrWhiteSpace(code) ? SupportedRoms.Json() : SupportedRoms.Lookup(code);
      return Stamp(JsonSerializer.Serialize(new { ok = true, text }, Json), GuiBridge.IsGuiRunning() ? "live" : "headless");
   }
```
(The gate's `result_text` returns `result.content[0].text` = the `{ok,text,mode}` JSON; `grep -ci BPRE0`/`Emerald` matches the embedded JSON inside `text`.)

- [ ] **Step 7: Add the resource (`HelpResources.cs`).** Next to the guide resource, add a method returning `SupportedRoms.Json()` with `UriTemplate="hexmaniac://supported-roms"`, `Name="HexManiacAdvance supported ROMs"`, `MimeType="application/json"` (mirror the existing guide resource's attribute + `TextResourceContents` shape exactly).

- [ ] **Step 8: Build MCP + GUI → 0 errors.**

- [ ] **Step 9: Run the gate** (no GUI) → ALL GREEN incl. the 5 new assertions; source md5 pristine.

- [ ] **Step 10: Commit.**
```bash
git add src/HexManiac.Mcp/resources/supported-roms.json src/HexManiac.Mcp/HexManiac.Mcp.csproj src/HexManiac.Mcp/SupportedRoms.cs src/HexManiac.Mcp/RomTools.cs src/HexManiac.Mcp/HelpResources.cs test/mcp-smoke.sh
git commit -m "MCP supported-roms reference: data + supported_roms tool + resource"
```

---

## Task 2: `identify_rom` tool (clean check + base detection)

**Files:** modify `src/HexManiac.Mcp/RomTools.cs`, `src/HexManiac.Mcp/AutomationPipeServer.cs`, `test/mcp-smoke.sh`.

**Interfaces:**
- Consumes: `SupportedRoms` (Task 1), `IDataModel.GetGameCode()`, `Force.Crc32.Crc32Algorithm`.
- Produces: `identify_rom(tab?, tabFile?)`; result `{ ok, headerCode, baseGame, revision, hmaSupport, isCleanDump, matchedNoIntro, md5, sha1, crc32, note, mode }`.

- [ ] **Step 1: Write the failing test (`test/mcp-smoke.sh`).** Bump tool count `24`→`25`. Add (operate on `$OR`, which the gate already wrote/edited earlier):
```bash
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":60,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":61,"method":"tools/call","params":{"name":"identify_rom","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":62,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":"61"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":63,"method":"tools/call","params":{"name":"identify_rom","arguments":{}}}'; sleep 1
```
(`$R` is the clean FireRed source path.) Assertions:
```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "25" ] && ok "tools/list shows 25 tools" || bad "tools/list"
[ "$(result_text 61 | jq -r '.isCleanDump' 2>/dev/null)" = "true" ] && ok "identify_rom clean FireRed" || bad "identify clean"
[ "$(result_text 61 | jq -r '.baseGame' 2>/dev/null | grep -ci 'FireRed')" -ge 1 ] && ok "identify_rom base FireRed" || bad "identify base"
[ "$(result_text 63 | jq -r '.isCleanDump' 2>/dev/null)" = "false" ] && ok "identify_rom detects edit" || bad "identify edited"
[ "$(result_text 63 | jq -r '.baseGame' 2>/dev/null | grep -ci 'FireRed')" -ge 1 ] && ok "identify_rom base after edit (hack base)" || bad "identify base after edit"
```

- [ ] **Step 2: Run the gate → RED** (`identify_rom` unknown; count 24≠25).

- [ ] **Step 3: Add `identify_rom` + `IdentifyRom(IDataModel)` helper (`RomTools.cs`).**
```csharp
   [McpServerTool(Name = "identify_rom")]
   [Description("Identify the loaded/open ROM: header game code, base game + revision, HMA support level, and whether it's a clean No-Intro dump or an edited/romhack (and what base it's built on). Hashes the in-memory ROM (unsaved edits read as not-clean).")]
   public string IdentifyRom(RomSession session,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("identify_rom", new Dictionary<string, object?>(), tab, tabFile, () => IdentifyResult(session.Require()));
   }

   private static object IdentifyResult(IDataModel model) {
      var bytes = model.RawData is byte[] b ? b : model.RawData.ToArray();
      var code = model.GetGameCode();
      var md5 = string.Concat(System.Security.Cryptography.MD5.HashData(bytes).Select(x => x.ToString("x2")));
      var sha1 = string.Concat(System.Security.Cryptography.SHA1.HashData(bytes).Select(x => x.ToString("X2")));
      var crc32 = Force.Crc32.Crc32Algorithm.Compute(bytes).ToString("X8");
      return RomReference.Match(code, md5, sha1, crc32);
   }
```
Add a `RomReference.Match` helper (in `SupportedRoms.cs` or a small new file) that parses `SupportedRoms.Json()` and returns the result object:
```csharp
      // in SupportedRoms.cs
      public static object Match(string gameCode, string md5, string sha1, string crc32) {
         using var doc = JsonDocument.Parse(Json());
         JsonElement? byCode = null, byMd5 = null;
         foreach (var section in new[] { "roms", "notSupported", "alsoSupported" }) {
            if (!doc.RootElement.TryGetProperty(section, out var arr)) continue;
            foreach (var e in arr.EnumerateArray()) {
               if (e.TryGetProperty("headerCode", out var c) && string.Equals(c.GetString(), gameCode, StringComparison.OrdinalIgnoreCase)) byCode = e.Clone();
               if (e.TryGetProperty("md5", out var m) && string.Equals(m.GetString(), md5, StringComparison.OrdinalIgnoreCase)) byMd5 = e.Clone();
            }
         }
         string Get(JsonElement? el, string p) => el is JsonElement j && j.TryGetProperty(p, out var v) ? v.GetString() : null;
         var baseGame = Get(byCode, "game");
         var note = baseGame == null
            ? $"Unrecognized header code '{gameCode}'; HMA opens this as a plain hex editor."
            : (byMd5 != null ? "Clean No-Intro dump." : "Header matches a supported base, but the bytes don't match any known clean dump — edited / romhack.");
         return new {
            ok = true,
            headerCode = gameCode,
            baseGame,
            revision = Get(byCode, "revision"),
            hmaSupport = Get(byCode, "hmaSupport"),
            isCleanDump = byMd5 != null,
            matchedNoIntro = Get(byMd5, "noIntroName"),
            md5, sha1, crc32, note
         };
      }
```
(`IDataModel`, `Force.Crc32` are available via the Core reference; add `using System.Linq;` / `using System.Text.Json;` as needed. `MD5.HashData`/`SHA1.HashData` are static .NET APIs.)

- [ ] **Step 4: Add the live `identify_rom` pipe case (`AutomationPipeServer.cs`), before `default:`.**
```csharp
            case "identify_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var bytes = vp.Model.RawData is byte[] b ? b : System.Linq.Enumerable.ToArray(vp.Model.RawData);
               var code = vp.Model.GetGameCode();
               var md5 = string.Concat(System.Security.Cryptography.MD5.HashData(bytes).Select(x => x.ToString("x2")));
               var sha1 = string.Concat(System.Security.Cryptography.SHA1.HashData(bytes).Select(x => x.ToString("X2")));
               var crc32 = Force.Crc32.Crc32Algorithm.Compute(bytes).ToString("X8");
               return Ok(HavenSoft.HexManiac.Mcp.SupportedRoms.Match(code, md5, sha1, crc32));
            }
```
(Ensure `using System.Linq;` and the `HavenSoft.HexManiac.Core` using for `GetGameCode` are present in the file; `GetGameCode` is an extension on `IReadOnlyList<byte>`/`IDataModel`.)

- [ ] **Step 5: Build MCP + GUI → 0 errors.**

- [ ] **Step 6: Run the gate** (no GUI) → ALL GREEN incl. the 5 new assertions; source md5 pristine (`e26ee0d44e809351c8ce2d73c7400cdd`). Note id:61 identifies the freshly-opened clean `$R`; id:62 edits it; id:63 re-identifies → not clean, base still FireRed.

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.Mcp/AutomationPipeServer.cs src/HexManiac.Mcp/SupportedRoms.cs test/mcp-smoke.sh
git commit -m "MCP identify_rom: clean-dump check + base-game/romhack detection"
```

---

## Task 3: docs

**Files:** create `docs/SUPPORTED-ROMS.md`; modify `README.md`, `BUILD.md`, `docs/MCP.md`.

- [ ] **Step 1:** Create `docs/SUPPORTED-ROMS.md` — a human-readable rendering of `supported-roms.json`: a table (Game, Revision, Header code, No-Intro name, MD5, SHA1, CRC32, HMA support tier) for the 9 ROMs, plus the recognition note and the not-supported list. State it is generated from `src/HexManiac.Mcp/resources/supported-roms.json` (the source of truth). Use the checksums from that JSON verbatim.
- [ ] **Step 2:** `README.md` — add a line linking `docs/SUPPORTED-ROMS.md` ("Which ROMs are supported, with clean-dump checksums: see docs/SUPPORTED-ROMS.md, or call the `supported_roms` / `identify_rom` MCP tools").
- [ ] **Step 3:** `BUILD.md` + `docs/MCP.md` — document `supported_roms` and `identify_rom` (+ the `hexmaniac://supported-roms` resource); add both to the `docs/MCP.md` Tools index (now 25 tools). Rebuild MCP so the embedded `docs/MCP.md` updates.
- [ ] **Step 4:** Run `bash test/mcp-smoke.sh` (no GUI) → ALL GREEN (25 tools); source md5 pristine.
- [ ] **Step 5:** `git add docs/SUPPORTED-ROMS.md README.md BUILD.md docs/MCP.md && git commit -m "Docs: supported_roms + identify_rom + SUPPORTED-ROMS.md"`.

---

## Self-Review notes
- **Spec coverage:** data file (T1 S3) + embed (S4) + loader (S5) + tool (S6) + resource (S7); identify_rom both backends w/ clean-check + base detection (T2); tracked human doc + README/BUILD/MCP.md (T3). Covered.
- **Headless invariant:** gate runs T1 S9, T2 S6, T3 S4 + source md5 each.
- **Type consistency:** `SupportedRoms.Json()/Lookup(code)/Match(code,md5,sha1,crc32)`; `identify_rom` result keys match the gate's `jq` (.isCleanDump,.baseGame); tool count 23→24→25; `GetGameCode()` + `Force.Crc32` from Core reference.
- **Safety:** tests on `$R`/`$OR`, never overwrite the source; identify hashes in-memory (no writes).
