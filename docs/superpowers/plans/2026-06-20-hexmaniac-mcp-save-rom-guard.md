# `save_rom` Overwrite Guard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or executing-plans). Checkbox (`- [ ]`) steps.

**Goal:** `save_rom` must not silently overwrite the loaded/open ROM — it requires `outPath` (save a copy) or `overwrite:true` (save over the source), else it errors. Applies to both backends.

**Architecture:** Add a `bool overwrite=false` to the `save_rom` tool; branch in both the headless fallback (`SaveRomHeadless`) and the live pipe handler: outPath→copy, overwrite→in-place save, neither→guard error. No Core changes.

**Tech Stack:** C# / .NET (MCP net8.0, WPF net6.0), bash + jq gates.

## Global Constraints

- Headless `test/mcp-smoke.sh` stays ALL GREEN; the existing `outPath` save is unchanged.
- Tests MUST NOT overwrite the real source `test/roms/firered.gba` (only `outPath` copies or the throwaway `test/.tmp/firered.edited.gba`).
- No new tool (save_rom modified in place); tool count stays 20. Results carry `mode`.
- Reuse `Dispatch`/`Stamp`, `RomAutomation.Err`, `ResolveTab`, `NoTab`, `Ok`, `Str`/`StrOrNull`, `Bool`, `GuiFileSystem`.
- Build: MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; GUI `dotnet build -c Release -p:PlatformTarget=x64`. Headless gate with NO GUI (`taskkill //F //IM HexManiacAdvance.exe`; `//IM HexManiac.Mcp.exe`).

---

## File Structure
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — `SaveRom` tool gains `overwrite`; `SaveRomHeadless` branches.
- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — `save_rom` case: outPath copy / overwrite / guard.
- **Modify `test/mcp-smoke.sh`** and **`test/mcp-live-smoke.sh`** — guard + copy + (headless-only) overwrite tests.
- **Modify `BUILD.md`** — document the guard.

---

## Task 1: save_rom overwrite guard

**Files:**
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `test/mcp-smoke.sh`
- Modify: `test/mcp-live-smoke.sh`

**Interfaces:**
- Consumes: `session.Require()`, `session.RomPath`, `session.RequireViewPort().ChangeHistory.ChangeCompleted()`, `vp.Model.RawData`, `vp.Save`, `GuiFileSystem()`, `Bool`, `Dispatch`, `RomAutomation.Err`.
- Produces: `save_rom(outPath=null, overwrite=false, tab, tabFile)`; result `{ ok, path|saved, overwrote, mode }` or guard `{ error }`.

- [ ] **Step 1: Write the failing tests.**

In `test/mcp-smoke.sh`, after the last driver line (inside the `{ ... } | "./$EXE"` block) add:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"save_rom","arguments":{}}}'; sleep 1
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":44,"method":"tools/call","params":{"name":"save_rom","arguments":{"overwrite":true}}}'; sleep 2
```
(`$OR` is the existing throwaway `test/.tmp/firered.edited.gba` path var.) Assertions:
```bash
[ "$(result_text 42 | jq -r '.error' 2>/dev/null | grep -ci 'Refusing to overwrite')" -ge 1 ] && ok "save_rom guards in-place overwrite" || bad "save_rom guard"
[ "$(result_text 44 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "save_rom overwrite=true saves in place" || bad "save_rom overwrite"
[ "$(result_text 44 | jq -r '.overwrote' 2>/dev/null)" = "true" ] && ok "save_rom reports overwrote" || bad "save_rom overwrote flag"
```

In `test/mcp-live-smoke.sh`, near the top where paths are defined add a temp copy target, and (inside the driver block, after the last existing line) add the copy + guard calls. First add a path var (next to `ROMW`):
```bash
COPYW="$(cygpath -m "$(pwd)/test/.tmp/live-savecopy.gba")"
```
Then in the driver heredoc (paths in JSON must be escaped like the existing `ROMW_JSON`; reuse that helper pattern):
```bash
  COPY_JSON=$(printf '%s' "$COPYW" | sed 's/\\/\\\\/g')
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"save_rom\",\"arguments\":{\"outPath\":\"$COPY_JSON\"}}}"; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"save_rom","arguments":{}}}'; sleep 1
```
(Use the next free ids after the current last one — if 20/21 are taken, use the next free integers; keep them unique.) Assertions:
```bash
[ "$(rt 20 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "live save_rom outPath copy ok" || bad "live save copy"
[ "$(rt 21 | jq -r '.error' 2>/dev/null | grep -ci 'Refusing to overwrite')" -ge 1 ] && ok "live save_rom guards in-place" || bad "live save guard"
```
Also, after the gate's run, the copy file should exist; optionally assert `[ -s test/.tmp/live-savecopy.gba ]`. Do NOT add an `overwrite:true` live call.

- [ ] **Step 2: Run both gates to verify failure.** `bash test/mcp-smoke.sh` (no GUI): id:42 currently SAVES in place (no error) → assertion fails; id:44 has no `overwrote` field. `bash test/mcp-live-smoke.sh`: id:20 (outPath) currently ignored → vp.Save overwrites the tab file, `.ok` true but no copy at COPYW → copy assertion fails; id:21 currently overwrites silently (no error) → guard assertion fails.

- [ ] **Step 3: Update `SaveRom` + `SaveRomHeadless` in `RomTools.cs`.** Replace both:

```csharp
   [McpServerTool(Name = "save_rom")]
   [Description("Write the ROM to disk. Pass outPath to save a COPY there; pass overwrite=true (no outPath) to save over the loaded/open ROM. With neither, it refuses (won't silently overwrite the source). Live targets the resolved tab; else headless.")]
   public string SaveRom(
      RomSession session,
      [Description("Output path to save a COPY to")] string? outPath = null,
      [Description("Save over the loaded/open ROM in place (only used when outPath is omitted)")] bool overwrite = false,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["overwrite"] = overwrite };
      if (!string.IsNullOrEmpty(outPath)) p["outPath"] = outPath;
      return Dispatch("save_rom", p, tab, tabFile, () => SaveRomHeadless(session, outPath, overwrite));
   }

   private static object SaveRomHeadless(RomSession session, string? outPath, bool overwrite) {
      var model = session.Require();
      session.RequireViewPort().ChangeHistory.ChangeCompleted();
      if (!string.IsNullOrEmpty(outPath)) {
         File.WriteAllBytes(outPath, model.RawData);
         return new { ok = true, path = outPath, overwrote = false, length = model.RawData.Length };
      }
      if (!overwrite)
         return RomAutomation.Err("Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source.");
      if (string.IsNullOrEmpty(session.RomPath)) return RomAutomation.Err("No loaded ROM path to overwrite.");
      File.WriteAllBytes(session.RomPath, model.RawData);
      return new { ok = true, path = session.RomPath, overwrote = true, length = model.RawData.Length };
   }
```

- [ ] **Step 4: Update the live `save_rom` pipe case in `AutomationPipeServer.cs`.** Replace the `case "save_rom":` block:

```csharp
            case "save_rom": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               var outPath = StrOrNull(p, "outPath");
               if (!string.IsNullOrEmpty(outPath)) {
                  System.IO.File.WriteAllBytes(outPath, vp.Model.RawData);
                  return Ok(new { ok = true, path = outPath, overwrote = false });
               }
               if (!Bool(p, "overwrite", false))
                  return new AutoResponse(false, null, "Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source.");
               vp.Save.Execute(GuiFileSystem());
               return Ok(new { ok = true, saved = vp.FullFileName ?? vp.Name, overwrote = true });
            }
```

- [ ] **Step 5: Build MCP + GUI.** Both → 0 errors.

- [ ] **Step 6: Run the headless gate.** No GUI. Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN`, incl. `save_rom guards in-place overwrite`, `save_rom overwrite=true saves in place`, `save_rom reports overwrote`; the existing `save_rom wrote file` (outPath) assertion still passes. Confirm `test/roms/firered.gba` md5 is unchanged afterward (`md5sum test/roms/firered.gba` == `e26ee0d44e809351c8ce2d73c7400cdd`) — the gate must not have touched the source.

- [ ] **Step 7: Run the live gate.** Run: `bash test/mcp-live-smoke.sh` — Expected: `LIVE GREEN`, incl. `live save_rom outPath copy ok` and `live save_rom guards in-place`. After it, confirm `md5sum test/roms/firered.gba` is still `e26ee0d44e809351c8ce2d73c7400cdd` (live gate didn't overwrite the source). (Re-run once if only the first-run `list_open_roms` timing flake appears.)

- [ ] **Step 8: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-smoke.sh test/mcp-live-smoke.sh
git commit -m "save_rom: guard in-place overwrite (require outPath or overwrite=true)"
```

---

## Task 2: Docs

**Files:** Modify `BUILD.md`

- [ ] **Step 1:** Update the `save_rom` bullet under "### ROM/tab lifecycle":
```markdown
- `save_rom` — pass `outPath` to save a COPY, or `overwrite=true` to save over the
  loaded/open ROM. With neither it refuses (won't silently overwrite the source).
```
- [ ] **Step 2:** `git add BUILD.md && git commit -m "Docs: save_rom overwrite guard"`.

---

## Self-Review notes
- **Spec coverage:** outPath copy (both modes), overwrite in-place (both), guard error (both), `overwrote` flag, `mode`; gates incl. md5 source-integrity check; docs. Covered.
- **Headless invariant:** existing outPath save unchanged; gate run T1 Step 6 + source md5 check.
- **Safety:** no test overwrites the real source — headless overwrite test targets the throwaway `$OR`; live tests use a copy path + the guard error (no live overwrite=true). Source md5 re-checked after both gates.
- **Type consistency:** `SaveRomHeadless(session, outPath, overwrite)`; pipe reads `outPath`(string)/`overwrite`(Bool); result keys `ok/path|saved/overwrote(/length)`; no new tool (count 20).
