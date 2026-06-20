# MCP `duplicate_tab` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a live-only `duplicate_tab` MCP tool that opens a second tab on the same ROM (the GUI's Ctrl+T), so a single ROM can have multiple tabs and `close_rom` closes all of them.

**Architecture:** A new pipe handler resolves a tab, calls `ViewPort.CreateDuplicate()` (a second `ViewPort` over the same `Model`/`history`), and `editor.Add`s it; the MCP tool routes through the existing `Dispatch` (live-only; headless returns a clear error). No Core changes.

**Tech Stack:** C# / .NET (WPF net6.0, MCP net8.0), `System.Text.Json`, ModelContextProtocol 1.4.0, bash + jq gates.

## Global Constraints

- Headless must keep working unchanged; `test/mcp-smoke.sh` stays ALL GREEN every task.
- GUI access ONLY on the WPF Dispatcher thread (pipe `Handle` already runs inside `Application.Current.Dispatcher.Invoke`).
- Every MCP tool result includes `"mode": "live" | "headless"` via the existing `Dispatch`/`Stamp`.
- `duplicate_tab` is live-only → clear `{error}` in headless. Never crash (no open tab; `!CanDuplicate`).
- The duplicate shares the source tab's `Model` AND `history` (so `close_rom` groups them, and they share unsaved state).
- Reuse existing helpers: `AutomationPipeServer` `ResolveTab`, `Ok`, `NoTab`, `editor`; `RomTools.Dispatch`; `RomAutomation.Err`. The headless gate tool-count assertion is currently `19`.
- Build: GUI `dotnet build -c Release -p:PlatformTarget=x64`; MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`. Run the headless gate with NO GUI running (`taskkill //F //IM HexManiacAdvance.exe`; `//IM HexManiac.Mcp.exe`).

---

## File Structure

- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — add the `duplicate_tab` pipe case.
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — add the `duplicate_tab` MCP tool.
- **Modify `test/mcp-smoke.sh`** — tool count 19→20; `duplicate_tab` live-only error.
- **Modify `test/mcp-live-smoke.sh`** — duplicate → two tabs → `close_rom` closes both.
- **Modify `BUILD.md`** — document `duplicate_tab`.

---

## Task 1: `duplicate_tab` tool (live-only)

**Files:**
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `test/mcp-smoke.sh`
- Modify: `test/mcp-live-smoke.sh`

**Interfaces:**
- Consumes: `ResolveTab`, `Ok`, `NoTab`, `editor` (`EditorViewModel.Add(ITabContent)`), `ViewPort.CanDuplicate` (`bool`), `ViewPort.CreateDuplicate()` (`ViewPort`, same `Model`/`history`), `ViewPort.FullFileName`/`Name`; `Dispatch`, `RomAutomation.Err`.
- Produces: pipe method `duplicate_tab`; MCP tool `duplicate_tab(tab=null, tabFile=null)`. Result `{ ok, index, file, mode }` (live) or `{ error }`.

- [ ] **Step 1: Write the failing tests.**

In `test/mcp-smoke.sh`: bump the tool-count assertion `19`→`20`:
```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "20" ] && ok "tools/list shows 20 tools" || bad "tools/list"
```
Add a driver call after the last existing driver line (the `id:34` close_rom line):
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":35,"method":"tools/call","params":{"name":"duplicate_tab","arguments":{}}}'; sleep 1
```
Add the assertion (headless = live-only error):
```bash
[ "$(result_text 35 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "duplicate_tab live-only in headless" || bad "duplicate_tab headless error"
```

In `test/mcp-live-smoke.sh`, after the last driver line (`id:16` list_open_roms), add (duplicate the open ROM's tab, list, then close_rom closes both — `force:true` because an earlier step (`id:4`) dirtied the tab and the duplicate shares its history):
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":17,"method":"tools/call","params":{"name":"duplicate_tab","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":18,"method":"tools/call","params":{"name":"list_open_roms","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":19,"method":"tools/call","params":{"name":"close_rom","arguments":{"tab":0,"force":true}}}'; sleep 1
```
Add assertions (after the existing ones):
```bash
[ "$(rt 17 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "duplicate_tab mode=live" || bad "duplicate_tab not live"
[ "$(rt 17 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "duplicate_tab ok" || bad "duplicate_tab ok"
[ "$(rt 18 | jq -r '.tabs | length' 2>/dev/null)" = "2" ] && ok "two tabs after duplicate" || bad "duplicate did not add tab"
[ "$(rt 19 | jq -r '.closedCount' 2>/dev/null)" = "2" ] && ok "close_rom closed both duplicate tabs" || bad "close_rom closedCount"
```

- [ ] **Step 2: Run both gates to verify failure.** `bash test/mcp-smoke.sh` (no GUI) FAILs (`duplicate_tab` unknown; count 19≠20). `bash test/mcp-live-smoke.sh` FAILs (duplicate_tab unknown → headless fallback, not `mode:live`; only 1 tab; close_rom closedCount 1 not 2).

- [ ] **Step 3: Add the pipe handler.** In `src/HexManiac.WPF/AutomationPipeServer.cs`, before `default:`:

```csharp
            case "duplicate_tab": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               if (!vp.CanDuplicate) return new AutoResponse(false, null, $"Tab '{vp.FullFileName ?? vp.Name}' cannot be duplicated.");
               var child = vp.CreateDuplicate();
               editor.Add(child);
               int index = -1, i = 0;
               foreach (var t in editor) { if (ReferenceEquals(t, child)) { index = i; break; } i++; }
               return Ok(new { ok = true, index, file = child.FullFileName ?? child.Name });
            }
```

- [ ] **Step 4: Add the MCP tool.** In `src/HexManiac.Mcp/RomTools.cs` (next to `close_tab`/`close_rom`):

```csharp
   [McpServerTool(Name = "duplicate_tab")]
   [Description("Open a second tab on the same ROM as the resolved tab (like Ctrl+T) — shares the ROM's model and undo history; close_rom then closes all such tabs. Live GUI only.")]
   public string DuplicateTab(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("duplicate_tab", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("duplicate_tab requires the live GUI."));
   }
```

- [ ] **Step 5: Build GUI + MCP.** `dotnet build -c Release -p:PlatformTarget=x64` and `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`. Expected: 0 errors. (Kill any running GUI/MCP first if a build locks.)

- [ ] **Step 6: Run the headless gate.** No GUI. Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN`, incl. `tools/list shows 20 tools` and `duplicate_tab live-only in headless`; all prior assertions still pass.

- [ ] **Step 7: Run the live gate.** Run: `bash test/mcp-live-smoke.sh` — Expected: `LIVE GREEN`, incl. `duplicate_tab mode=live`, `duplicate_tab ok`, `two tabs after duplicate`, `close_rom closed both duplicate tabs`. (If only the first-run `list_open_roms` timing flake appears, re-run once after `taskkill //F //IM HexManiacAdvance.exe`; `//IM HexManiac.Mcp.exe`.)

- [ ] **Step 8: Commit.**
```bash
git add src/HexManiac.WPF/AutomationPipeServer.cs src/HexManiac.Mcp/RomTools.cs test/mcp-smoke.sh test/mcp-live-smoke.sh
git commit -m "MCP duplicate_tab tool (second tab on the same ROM, live-only)"
```

---

## Task 2: Docs

**Files:**
- Modify: `BUILD.md`

- [ ] **Step 1: Document `duplicate_tab`.** In `BUILD.md`, under the "### ROM/tab lifecycle" subsection, add a bullet:

```markdown
- `duplicate_tab` — open a second tab on the resolved tab's ROM (like Ctrl+T),
  sharing its model and undo history. **Live only.** `close_rom` then closes all
  tabs of that ROM at once.
```

- [ ] **Step 2: Commit.**
```bash
git add BUILD.md
git commit -m "Docs: duplicate_tab in BUILD.md"
```

---

## Self-Review notes

- **Spec coverage:** `duplicate_tab(tab?, tabFile?)` via `CreateDuplicate`+`Add` (T1); `CanDuplicate` guard + live-only error (T1); shares model/history so `close_rom` closes both — asserted in the live gate (T1 Step 1/7); `mode` via Dispatch; tool count 19→20 (T1); headless live-only error (T1); docs (T2). All covered.
- **Headless invariant:** `test/mcp-smoke.sh` run in T1 Step 6.
- **Type consistency:** pipe `duplicate_tab` returns `{ ok, index, file }` (Dispatch adds `mode`); MCP tool routes via `Dispatch` with the live-only `Err` fallback, matching `close_tab`/`close_rom`. `CreateDuplicate()`/`CanDuplicate`/`editor.Add` signatures verified against `ViewPort.cs:857-863` / `EditorViewModel.Add`.
- **Test note:** the live `close_rom` uses `force:true` because the gate's earlier `id:4` write dirtied tab 0 and the duplicate shares that history; without `force` the unsaved guard would (correctly) refuse, which would be a false failure.
