# MCP `launch_rom` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or executing-plans). Checkbox (`- [ ]`) steps.

**Goal:** A `launch_rom` tool that shell-opens the on-disk ROM in the OS default program (HexManiacAdvance's play button), refusing if there are unsaved edits unless `force=true`, and reporting which file it launched.

**Architecture:** Headless launches via `HavenSoft.HexManiac.Core.NativeProcess.Start(session.RomPath)` (the same shell-open HMA uses); live routes to a pipe handler that calls `GuiFileSystem().LaunchProcess(vp.FullFileName)`. Both gate on `ChangeHistory.HasDataChange` unless `force`. No Core changes.

**Tech Stack:** C# / .NET (MCP net8.0, WPF net6.0), bash + jq gate.

## Global Constraints
- Headless `test/mcp-smoke.sh` stays ALL GREEN. The gate MUST NOT spawn an emulator: only the unsaved-refuse path is tested (no actual launch); the happy path is manual/live.
- Tests must not modify `test/roms/firered.gba` (md5 `e26ee0d44e809351c8ce2d73c7400cdd`); use the throwaway `$OR` (`test/.tmp/firered.edited.gba`).
- Results carry `mode`. Reuse `Dispatch`/`Stamp`, `RomAutomation.Err`, `ResolveTab`/`NoTab`/`Ok`/`Bool`/`GuiFileSystem`.
- `NativeProcess` is `HavenSoft.HexManiac.Core.NativeProcess` (fully-qualify to avoid a new using). `ChangeHistory` is on `IViewPort`/`ViewPort`.
- Build: MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; GUI `dotnet build -c Release -p:PlatformTarget=x64`. Headless gate with NO GUI.

---

## File Structure
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — `launch_rom` tool + `LaunchRomHeadless`.
- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — `launch_rom` case.
- **Modify `test/mcp-smoke.sh`** — unsaved-refuse assertion; tool count → 23.
- **Modify `BUILD.md`, `docs/MCP.md`** — document `launch_rom`.

---

## Task 1: `launch_rom` tool + pipe handler

**Files:** Modify `src/HexManiac.Mcp/RomTools.cs`, `src/HexManiac.WPF/AutomationPipeServer.cs`, `test/mcp-smoke.sh`.

**Interfaces:**
- Produces: `launch_rom(tab?, tabFile?, force=false)` MCP tool + pipe method. Result `{ ok, launched, mode }` or `{ error }`.

- [ ] **Step 1: Write the failing test — `test/mcp-smoke.sh`.** Bump tool count `22`→`23`. After the last driver line (operate on `$OR`):
```bash
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":53,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":54,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":"55"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":55,"method":"tools/call","params":{"name":"launch_rom","arguments":{}}}'; sleep 1
```
Assertions:
```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "23" ] && ok "tools/list shows 23 tools" || bad "tools/list"
[ "$(result_text 55 | jq -r '.error' 2>/dev/null | grep -ci 'unsaved changes')" -ge 1 ] && ok "launch_rom refuses when unsaved" || bad "launch_rom unsaved guard"
```
(id:54 dirties the freshly-opened `$OR`; id:55 `launch_rom` with no force returns the unsaved error and spawns nothing. Do NOT add a force/clean launch_rom call — it would shell-open a .gba.)

- [ ] **Step 2: Run the gate → RED.** `bash test/mcp-smoke.sh` (no GUI): `launch_rom` unknown (count 22≠23); id:55 has no error field.

- [ ] **Step 3: Add the `launch_rom` MCP tool + `LaunchRomHeadless` (`RomTools.cs`).**
```csharp
   [McpServerTool(Name = "launch_rom")]
   [Description("Launch the ROM in your default GBA program (like HexManiacAdvance's play button) by shell-opening the on-disk file. Refuses if the ROM has unsaved edits unless force=true (launches the last-saved file). Returns the launched path. Live or headless.")]
   public string LaunchRom(RomSession session,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Launch the last-saved file even if there are unsaved edits")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("launch_rom", p, tab, tabFile, () => LaunchRomHeadless(session, force));
   }

   private static object LaunchRomHeadless(RomSession session, bool force) {
      var path = session.RomPath;
      if (string.IsNullOrEmpty(path) || !File.Exists(path))
         return RomAutomation.Err("No saved ROM on disk to launch.");
      if (session.RequireViewPort().ChangeHistory.HasDataChange && !force)
         return RomAutomation.Err("ROM has unsaved changes; save first (save_rom), or pass force=true to launch the last-saved file on disk.");
      var full = Path.GetFullPath(path);
      try {
         HavenSoft.HexManiac.Core.NativeProcess.Start(full);
      } catch (System.ComponentModel.Win32Exception ex) {
         return RomAutomation.Err($"Could not launch '{full}': {ex.Message} (is a program associated with .gba?).");
      }
      return new { ok = true, launched = full };
   }
```

- [ ] **Step 4: Add the `launch_rom` pipe case (`AutomationPipeServer.cs`), before `default:`.**
```csharp
            case "launch_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var file = vp.FullFileName;
               if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file))
                  return new AutoResponse(false, null, "This tab has no saved on-disk ROM to launch.");
               if (vp.ChangeHistory.HasDataChange && !Bool(p, "force", false))
                  return new AutoResponse(false, null, "ROM has unsaved changes; save first (save_rom), or pass force=true to launch the last-saved file on disk.");
               var full = System.IO.Path.GetFullPath(file);
               GuiFileSystem().LaunchProcess(full);
               return Ok(new { ok = true, launched = full });
            }
```

- [ ] **Step 5: Build MCP + GUI → 0 errors.**

- [ ] **Step 6: Run the headless gate.** `bash test/mcp-smoke.sh` (no GUI) → `ALL GREEN` incl. `tools/list shows 23 tools` and `launch_rom refuses when unsaved`; all prior pass. Then `md5sum test/roms/firered.gba` == `e26ee0d44e809351c8ce2d73c7400cdd`. (No emulator should have been spawned — only the refuse path ran.)

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-smoke.sh
git commit -m "MCP launch_rom tool (shell-open ROM like the play button)"
```

---

## Task 2: Docs
**Files:** Modify `BUILD.md`, `docs/MCP.md`.
- [ ] **Step 1:** In `BUILD.md` (ROM/tab lifecycle) and `docs/MCP.md` (lifecycle section + Tools index; now 23 tools) document `launch_rom` (shell-opens the ROM in the default GBA program like the play button; needs it saved or `force=true`; returns the launched path). Rebuild MCP so the embedded `docs/MCP.md` updates; run `bash test/mcp-smoke.sh` (no GUI) → ALL GREEN (23/23); firered md5 pristine.
- [ ] **Step 2:** `git add BUILD.md docs/MCP.md && git commit -m "Docs: launch_rom"` (standard trailer).

---

## Self-Review notes
- **Spec coverage:** path resolution both modes; unsaved guard + force; shell-open (headless NativeProcess.Start / live LaunchProcess); launched-path result; mode; gate tests refuse-path only (no spawn); docs incl. embedded MCP.md. Covered.
- **Headless invariant:** gate run T1 Step 6 + md5; only the refuse path runs (no emulator spawned).
- **Type consistency:** `launch_rom` result `{ ok, launched, mode }` / `{ error }`; `Bool(p,"force",false)` live; `force` headless; `NativeProcess.Start` fully-qualified; tool count 22→23 (only launch_rom added).
- **Safety:** no test spawns a process or modifies the source ROM (refuse path on the throwaway $OR).
