# Live GUI Goto / Navigation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the MCP server navigate the running HexManiacAdvance GUI to a shortcut label (e.g. `Pokemon`), anchor name, or hex address, and add a read-only `list_shortcuts` to discover the available shortcut labels.

**Architecture:** Two new MCP tools (`goto`, `list_shortcuts`) follow the existing live/headless pattern: route to the GUI automation pipe when reachable, else compute headless. The pipe gains a `goto` handler (resolves the target, then runs `ViewPort.Goto.Execute` on the UI thread) and a `list_shortcuts` handler. Shortcut listing lives in `HexManiac.Core/RomAutomation` so both backends share it; navigation stays in the WPF layer because `Goto` is a `ViewPort` command.

**Tech Stack:** C# / .NET (Core net6.0, WPF net6.0, MCP net8.0), `System.IO.Pipes` named pipes, `System.Text.Json`, ModelContextProtocol; bash + jq smoke gates.

## Global Constraints

- Headless must keep working unchanged; `test/mcp-smoke.sh` stays ALL GREEN every task.
- GUI model access ONLY on the WPF Dispatcher thread (already handled: `Handle` runs inside `Application.Current.Dispatcher.Invoke`).
- Pipe name: `HexManiacAdvance.Automation`. Localhost / same-user, no auth.
- Every MCP tool result includes `"mode": "live" | "headless"` (via the existing `Stamp`/`Dispatch` helpers).
- `goto` is live-only; in headless mode it returns a clear error (no crash, no silent no-op).
- `goto` validates the target before executing: a shortcut label resolves to its anchor; otherwise the target must be a known anchor (`GetAddressFromAnchor != Pointer.NULL`) or a hex address (`TryParseHex`), else return an error.
- Build commands unchanged: GUI = `dotnet build -c Release -p:PlatformTarget=x64` (repo root, SDK 6); MCP = `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` (SDK 8).

---

## File Structure

- **Modify `src/HexManiac.Core/RomAutomation.cs`** — add `ListShortcuts(IDataModel)` returning `{ count, shortcuts: [{ display, anchor }] }`. Shared by both backends.
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — add `list_shortcuts` and `goto` MCP tools, routed through the existing `Dispatch`.
- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — add `list_shortcuts` and `goto` cases to `Handle`, plus a `TryResolveGotoTarget` helper.
- **Modify `test/mcp-smoke.sh`** — headless gate: tool count 8 → 10; assert headless `list_shortcuts` shape and `goto` live-only error.
- **Modify `test/mcp-live-smoke.sh`** — live gate: assert live `list_shortcuts` includes `Pokemon` and `goto Pokemon` succeeds.
- **Modify `BUILD.md`** — document the two new tools under the live-mode section.

---

## Task 1: Core `ListShortcuts` + MCP `list_shortcuts` tool (headless path)

**Files:**
- Modify: `src/HexManiac.Core/RomAutomation.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Test: `test/mcp-smoke.sh`

**Interfaces:**
- Consumes: `IDataModel.GotoShortcuts` (`IReadOnlyList<GotoShortcutModel>`, each with `DisplayText` and `GotoAnchor`); existing `RomTools.Dispatch(method, liveParams, tab, tabFile, headless)`; `RomSession.Require()`.
- Produces: `static object RomAutomation.ListShortcuts(IDataModel model)` → `Dictionary` serializing to `{ "count": int, "shortcuts": [ { "display": string, "anchor": string } ] }`. New MCP tool `list_shortcuts(tab?, tabFile?)`.

- [ ] **Step 1: Write the failing test — extend `test/mcp-smoke.sh`.**

Change the tool-count assertion (currently line 62) from `8` to `9`:

```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "9" ] && ok "tools/list shows 9 tools" || bad "tools/list"
```

Add a `list_shortcuts` call to the driver block, immediately after the `id:13` `run_script` line (line 57), before the closing `}`:

```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{}}}'; sleep 2
```

Add assertions after the `run_script` assertion (after line 75):

```bash
# 7. list_shortcuts (headless): correct mode + array shape
[ "$(result_text 14 | jq -r '.mode' 2>/dev/null)" = "headless" ] && ok "list_shortcuts mode=headless" || bad "list_shortcuts mode"
[ "$(result_text 14 | jq -r '.shortcuts|type' 2>/dev/null)" = "array" ] && ok "list_shortcuts returns array" || bad "list_shortcuts shape"
```

- [ ] **Step 2: Run the gate to verify it fails.**

Run: `bash test/mcp-smoke.sh`
Expected: FAIL — `tools/list` expects 9 but sees 8, and the `list_shortcuts` assertions fail (unknown tool → `result_text 14` empty). Overall `FAIL >= 1`, no `ALL GREEN`.

- [ ] **Step 3: Implement `RomAutomation.ListShortcuts`.**

In `src/HexManiac.Core/RomAutomation.cs`, add this method to the `RomAutomation` class (after `ExportToFile`, before `Err`):

```csharp
public static object ListShortcuts(IDataModel model) {
   var shortcuts = model.GotoShortcuts
      .Select(s => new Dictionary<string, object?> { ["display"] = s.DisplayText, ["anchor"] = s.GotoAnchor })
      .ToList();
   return new Dictionary<string, object?> { ["count"] = shortcuts.Count, ["shortcuts"] = shortcuts };
}
```

(`System.Linq` and `HavenSoft.HexManiac.Core.Models` — where `GotoShortcutModel` lives — are already in scope in this file.)

- [ ] **Step 4: Implement the `list_shortcuts` MCP tool.**

In `src/HexManiac.Mcp/RomTools.cs`, add this tool after `ListOpenRoms` (after line 36):

```csharp
   [McpServerTool(Name = "list_shortcuts")]
   [Description("List the GUI 'Goto' shortcut buttons (e.g. Pokemon, Trainers) as {display, anchor}. Targets the GUI's active tab when live; else headless.")]
   public string ListShortcuts(
      RomSession session,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      return Dispatch("list_shortcuts", p, tab, tabFile, () => RomAutomation.ListShortcuts(session.Require()));
   }
```

- [ ] **Step 5: Build the MCP server.**

Run: `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 6: Run the gate to verify it passes.**

Run: `bash test/mcp-smoke.sh`
Expected: `ALL GREEN`. `tools/list shows 9 tools`, `list_shortcuts mode=headless`, and `list_shortcuts returns array` all PASS.

- [ ] **Step 7: Commit.**

```bash
git add src/HexManiac.Core/RomAutomation.cs src/HexManiac.Mcp/RomTools.cs test/mcp-smoke.sh
git commit -m "Add list_shortcuts (Core helper + MCP tool, headless path)"
```

---

## Task 2: MCP `goto` tool (headless live-only error path)

**Files:**
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Test: `test/mcp-smoke.sh`

**Interfaces:**
- Consumes: `RomTools.Dispatch`; `RomAutomation.Err(string)` → `Dictionary { ["error"] = message }`.
- Produces: new MCP tool `goto(target, tab?, tabFile?)`. Headless result: `{ "error": "goto requires the live GUI (no view to navigate in headless mode).", "mode": "headless" }`. Live result (added in Task 3): `{ "ok": true, "target", "resolved", "tab", "mode": "live" }`.

- [ ] **Step 1: Write the failing test — extend `test/mcp-smoke.sh`.**

Bump the tool-count assertion from `9` to `10`:

```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "10" ] && ok "tools/list shows 10 tools" || bad "tools/list"
```

Add a `goto` call to the driver block, right after the `id:14` `list_shortcuts` line:

```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"goto","arguments":{"target":"Pokemon"}}}'; sleep 2
```

Add assertions after the `list_shortcuts` assertions:

```bash
# 8. goto (headless): live-only error + correct mode
[ "$(result_text 15 | jq -r '.mode' 2>/dev/null)" = "headless" ] && ok "goto mode=headless" || bad "goto mode"
[ "$(result_text 15 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "goto headless live-only error" || bad "goto headless error"
```

- [ ] **Step 2: Run the gate to verify it fails.**

Run: `bash test/mcp-smoke.sh`
Expected: FAIL — `tools/list` expects 10 but sees 9; `goto` assertions fail (unknown tool → `result_text 15` empty). No `ALL GREEN`.

- [ ] **Step 3: Implement the `goto` MCP tool.**

In `src/HexManiac.Mcp/RomTools.cs`, add this tool immediately after the `ListShortcuts` tool from Task 1:

```csharp
   [McpServerTool(Name = "goto")]
   [Description("Navigate the live GUI to a target: a shortcut label (e.g. Pokemon), an anchor name (e.g. data.pokemon.stats), or a hex address. Live GUI only; headless returns an error.")]
   public string Goto(
      RomSession session,
      [Description("Shortcut label, anchor name, or hex address to navigate to")] string target,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["target"] = target };
      return Dispatch("goto", p, tab, tabFile,
         () => RomAutomation.Err("goto requires the live GUI (no view to navigate in headless mode)."));
   }
```

- [ ] **Step 4: Build the MCP server.**

Run: `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 5: Run the gate to verify it passes.**

Run: `bash test/mcp-smoke.sh`
Expected: `ALL GREEN`. `tools/list shows 10 tools`, `goto mode=headless`, and `goto headless live-only error` all PASS. (Headless invariant preserved.)

- [ ] **Step 6: Commit.**

```bash
git add src/HexManiac.Mcp/RomTools.cs test/mcp-smoke.sh
git commit -m "Add goto MCP tool (headless live-only error path)"
```

---

## Task 3: GUI pipe handlers `goto` + `list_shortcuts` (live path)

**Files:**
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Test: `test/mcp-live-smoke.sh`

**Interfaces:**
- Consumes: `RomAutomation.ListShortcuts(IDataModel)` (Task 1); `ViewPort.Goto` (`ICommand`); `ViewPort.Model` (`IDataModel`); `IDataModel.GotoShortcuts`, `IDataModel.GetAddressFromAnchor(ModelDelta, int, string)`; `Pointer.NULL` (namespace `HavenSoft.HexManiac.Core.ViewModels`, already imported); `NoDataChangeDeltaModel` (namespace `HavenSoft.HexManiac.Core.Models`, already imported); `TryParseHex` string extension (namespace `HavenSoft.HexManiac.Core.ViewModels`, already imported). Existing helpers `ResolveTab`, `Ok`, `NoTab`, `Str`.
- Produces: pipe methods `list_shortcuts` (`{ tab? }` → `RomAutomation.ListShortcuts`) and `goto` (`{ target, tab? }` → `{ ok, target, resolved, tab }`); private `static bool TryResolveGotoTarget(IDataModel, string, out string)`.

> **Operational note:** Building the GUI requires the running `HexManiacAdvance.exe` to be closed first (the exe is locked while running). The live gate script handles this with `taskkill` at its start.

- [ ] **Step 1: Write the failing test — extend `test/mcp-live-smoke.sh`.**

Add two calls to the driver block, right after the `id:5` `read_table` line (line 43), before the closing `}`:

```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"goto","arguments":{"target":"Pokemon"}}}'; sleep 1
```

Add assertions after the last existing assertion (after line 53):

```bash
[ "$(rt 6 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "list_shortcuts mode=live" || bad "list_shortcuts not live"
[ "$(rt 6 | jq -r '.shortcuts[].display' 2>/dev/null | grep -ci '^Pokemon$')" -ge 1 ] && ok "Pokemon shortcut listed" || bad "Pokemon shortcut missing"
[ "$(rt 7 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "goto mode=live" || bad "goto not live"
[ "$(rt 7 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "goto ok=true" || bad "goto not ok"
[ -n "$(rt 7 | jq -r '.resolved // empty' 2>/dev/null)" ] && ok "goto resolved non-empty" || bad "goto resolved empty"
```

- [ ] **Step 2: Run the live gate to verify it fails.**

Run: `bash test/mcp-live-smoke.sh`
Expected: FAIL — the pipe replies `Unknown method: list_shortcuts` / `Unknown method: goto`, so the MCP tools fall back to headless: `rt 6` reports `mode:"headless"` and `rt 7` reports `mode:"headless"` with the live-only error. The 5 new assertions fail; no `LIVE GREEN`.

- [ ] **Step 3: Add the pipe handlers in `AutomationPipeServer.Handle`.**

In `src/HexManiac.WPF/AutomationPipeServer.cs`, add these two cases to the `switch (req.Method)` in `Handle`, immediately before the `default:` case (line 113):

```csharp
            case "list_shortcuts": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               return Ok(RomAutomation.ListShortcuts(vp.Model));
            }
            case "goto": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               var target = Str(p, "target");
               if (string.IsNullOrEmpty(target))
                  return new AutoResponse(false, null, "Provide a 'target' (shortcut label, anchor, or address).");
               if (!TryResolveGotoTarget(vp.Model, target, out var resolved))
                  return new AutoResponse(false, null, $"Unknown goto target '{target}'. Use list_shortcuts, or a valid anchor/address.");
               vp.Goto.Execute(resolved);
               return Ok(new { ok = true, target, resolved, tab = vp.FullFileName ?? vp.Name });
            }
```

- [ ] **Step 4: Add the `TryResolveGotoTarget` helper.**

In the same file, add this private method next to the other helpers (e.g. after `ResolveTab`, before `Ok`):

```csharp
      // Resolve a goto target without mutating data. A shortcut DisplayText maps
      // to its GotoAnchor; otherwise the target must already be a known anchor or
      // a hex address (the inputs ViewPort.ExecuteGoto accepts), so we never
      // silently no-op on a bad target.
      private static bool TryResolveGotoTarget(IDataModel model, string target, out string resolved) {
         var shortcut = model.GotoShortcuts.FirstOrDefault(s =>
            string.Equals(s.DisplayText, target, StringComparison.OrdinalIgnoreCase));
         if (shortcut != null) { resolved = shortcut.GotoAnchor; return true; }
         resolved = target;
         if (model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, target) != Pointer.NULL) return true;
         if (target.TryParseHex(out _)) return true;
         return false;
      }
```

(`System.Linq` for `FirstOrDefault`, `System` for `StringComparison`, `HavenSoft.HexManiac.Core.Models`, and `HavenSoft.HexManiac.Core.ViewModels` are all already imported at the top of this file.)

- [ ] **Step 5: Build the GUI.**

Run: `dotnet build -c Release -p:PlatformTarget=x64`
Expected: `Build succeeded`. (If it fails with a file-lock error, close any running `HexManiacAdvance.exe` first: `taskkill //F //IM HexManiacAdvance.exe`.)

- [ ] **Step 6: Run the live gate to verify it passes.**

Run: `bash test/mcp-live-smoke.sh`
Expected: `LIVE GREEN`. The 5 new assertions PASS: `list_shortcuts mode=live`, `Pokemon shortcut listed`, `goto mode=live`, `goto ok=true`, `goto resolved non-empty` (alongside the existing live assertions). The script launches the GUI, drives the MCP server, and kills the GUI at the end.

- [ ] **Step 7: Confirm the headless gate still passes.**

Run: `bash test/mcp-smoke.sh`
Expected: `ALL GREEN` (no GUI running → `goto` still returns the headless live-only error; `list_shortcuts` still headless).

- [ ] **Step 8: Commit.**

```bash
git add src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-live-smoke.sh
git commit -m "GUI pipe: goto + list_shortcuts handlers (live navigation)"
```

---

## Task 4: Docs + redeploy & manual verification

**Files:**
- Modify: `BUILD.md`

- [ ] **Step 1: Document the new tools in `BUILD.md`.**

In the live-mode section of `BUILD.md`, add a short subsection (place it after the existing tool list / live-mode description):

```markdown
### Navigation tools

- `list_shortcuts` — lists the GUI's "Goto" shortcut buttons as `{ display, anchor }`
  (e.g. `Pokemon`, `Trainers`, `Moves`, `Items`, `Maps`). Works in both live and
  headless mode.
- `goto` — **live only.** Navigates the active GUI tab to a target: a shortcut
  label (e.g. `Pokemon`), an anchor name (e.g. `data.pokemon.stats`), or a hex
  address. In headless mode it returns an error (there is no view to navigate).
  `bash test/mcp-live-smoke.sh` exercises `goto Pokemon` against the running GUI.
```

- [ ] **Step 2: Commit the docs.**

```bash
git add BUILD.md
git commit -m "Docs: goto + list_shortcuts in BUILD.md live-mode section"
```

- [ ] **Step 3: Redeploy the GUI for live use (the user's original request).**

Close the currently-running GUI, rebuild, and relaunch it with the user's ROM:

```bash
taskkill //F //IM HexManiacAdvance.exe >/dev/null 2>&1 || true
dotnet build -c Release -p:PlatformTarget=x64
```

```powershell
Start-Process "Q:\Users\user\Projects\HexManiacAdvance-MCP\artifacts\HexManiac.WPF\bin\Release\net6.0-windows\HexManiacAdvance.exe" "C:\Users\user\Desktop\1636.gba"
```

- [ ] **Step 4: Reconnect the MCP client and run `goto`.**

The MCP server exe was rebuilt, so the connected MCP client must reconnect to expose the new `goto`/`list_shortcuts` tools (run `/mcp` to reconnect in the client). Then call `goto` with `target: "Pokemon"` and confirm:
- The response reports `"mode": "live"`, `"ok": true`, and a non-empty `resolved` anchor.
- **The GUI window visibly switches to the Pokémon table view.**

This is the manual confirmation that the feature satisfies the original "open the pokemon tab" request.

---

## Self-Review notes

- **Spec coverage:** `goto` tool + live routing + validation (T2, T3); `list_shortcuts` both modes (T1 headless, T3 live); shared `RomAutomation.ListShortcuts` in Core (T1); pipe handlers on UI thread (T3 — `Handle` already runs in `Dispatcher.Invoke`); target resolution shortcut→anchor + anchor/address validation (T3 `TryResolveGotoTarget`); `mode` field (all tools via `Dispatch`/`Stamp`); headless live-only error (T2); live + headless gates (T1–T3); docs (T4). All spec sections covered.
- **Headless invariant:** `test/mcp-smoke.sh` is run in T1, T2, and T3 (Step 7); stays ALL GREEN.
- **Type consistency:** `RomAutomation.ListShortcuts(IDataModel)` defined T1, called T1 (MCP headless) and T3 (pipe live) identically. `goto` JSON keys (`ok`, `target`, `resolved`, `tab`) defined in the T3 pipe handler match the `goto` live result described in T2's Interfaces. `TryResolveGotoTarget` signature is used only within `AutomationPipeServer` (T3). Tool count steps to 9 (T1) then 10 (T2), matching the two added tools.
- **Validation choice:** `TryResolveGotoTarget` uses `NoDataChangeDeltaModel` + `Pointer.NULL` + `TryParseHex` — exactly the acceptance checks `ViewPort.ExecuteGoto` itself uses — so it never rejects a target the GUI would have accepted as an anchor or address. Maps/docs targets are out of scope per the spec.
