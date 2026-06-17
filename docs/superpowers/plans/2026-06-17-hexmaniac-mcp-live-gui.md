# Live GUI ⇄ MCP Integration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the MCP server read/edit the ROMs open in a running HexManiacAdvance GUI (live, incl. unsaved edits) while keeping full headless operation when the GUI is closed.

**Architecture:** One MCP server, two backends chosen per-call: if the GUI's named-pipe automation server is reachable, forward tool calls to it (it runs them on the WPF UI thread against the open tabs); otherwise use the existing headless `RomSession`. Shared table/script logic lives in `HexManiac.Core` so both backends use one implementation.

**Tech Stack:** C# / .NET (Core net6.0, WPF net6.0, MCP net8.0), `System.IO.Pipes` named pipes, `System.Text.Json`, ModelContextProtocol 1.4.0.

## Global Constraints

- Headless must keep working unchanged; `test/mcp-smoke.sh` stays ALL GREEN every task.
- GUI model access ONLY on the WPF Dispatcher thread (`Application.Current.Dispatcher.Invoke`).
- Live writes go through the tab's `CurrentChange` token (so GUI undo works).
- Pipe name: `HexManiacAdvance.Automation`. Localhost/same-user, no auth (v1).
- Build commands unchanged: GUI = `dotnet build -c Release -p:PlatformTarget=x64` (repo root, SDK 6); MCP = `(cd src/HexManiac.Mcp && dotnet build -c Release)` (SDK 8).
- Every MCP tool result includes `"mode": "live" | "headless"`.

---

## File Structure

- Create `src/HexManiac.Core/RomAutomation.cs` — shared logic + DTOs + pipe envelope types (net6.0, referenced by both WPF and MCP).
- Create `src/HexManiac.WPF/AutomationPipeServer.cs` — named-pipe server; marshals to UI thread; operates on `EditorViewModel` tabs.
- Modify `src/HexManiac.WPF/Windows/App.xaml.cs` — start the pipe server after `MainWindow.Show()`.
- Create `src/HexManiac.Mcp/GuiBridge.cs` — pipe client + reachability check.
- Modify `src/HexManiac.Mcp/RomTools.cs` — delegate to `RomAutomation`; route to `GuiBridge` when live; add `tab` arg + `mode` field + `list_open_roms`.
- Create `test/mcp-live-smoke.sh` — live gate.
- Modify `BUILD.md` — document live mode + the live gate.

---

## Task 1: Extract shared `RomAutomation` into Core; headless uses it

**Files:**
- Create: `src/HexManiac.Core/RomAutomation.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Test: `test/mcp-smoke.sh` (existing regression gate)

**Interfaces:**
- Produces:
  - `record AutoRequest(string Method, JsonElement Params)` and `record AutoResponse(bool Ok, JsonElement? Result, string? Error)` — pipe envelope (System.Text.Json).
  - `static class RomAutomation` with:
    - `static object ListTables(IDataModel model, string? filter)`
    - `static object ReadTable(IDataModel model, string name, int start, int count)`
    - `static object WriteValue(IDataModel model, Func<ModelDelta> token, string table, int index, string field, int value)`
    - `static object ExportToFile(IDataModel model, string name, string outPath)`
    - All return plain anonymous-friendly objects (Dictionaries / records) that serialize to the SAME JSON the current tools emit.

- [ ] **Step 1: Create `RomAutomation.cs` with the read/list/write/export logic moved from `RomTools`.** Namespace `HavenSoft.HexManiac.Core.Models`. Port `FieldNames`, `ReadRows`, and the bodies of `ListTables`/`ReadTable`/`WriteValue`/`ExportTable` verbatim, parameterized by `IDataModel` (and `Func<ModelDelta>` for writes). Add the envelope records `AutoRequest`/`AutoResponse`.

```csharp
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Runs;

namespace HavenSoft.HexManiac.Core.Models {
   public record AutoRequest(string Method, JsonElement Params);
   public record AutoResponse(bool Ok, JsonElement? Result, string? Error);

   public static class RomAutomation {
      public static List<string> FieldNames(ModelTable t) =>
         t.Run.ElementContent.Where(s => !string.IsNullOrEmpty(s.Name)).Select(s => s.Name).ToList();

      public static List<Dictionary<string, object?>> ReadRows(ModelTable t, int start, int count) {
         var rows = new List<Dictionary<string, object?>>();
         int begin = Math.Max(0, start), end = Math.Min(t.Count, begin + Math.Max(0, count));
         for (int i = begin; i < end; i++) {
            var e = t[i];
            var row = new Dictionary<string, object?> { ["index"] = i };
            foreach (var seg in t.Run.ElementContent) {
               if (string.IsNullOrEmpty(seg.Name)) continue;
               try { row[seg.Name] = seg.Type == ElementContentType.PCS ? e.GetStringValue(seg.Name) : e.GetValue(seg.Name); }
               catch { row[seg.Name] = null; }
            }
            rows.Add(row);
         }
         return rows;
      }

      public static object ListTables(IDataModel model, string? filter) {
         var names = model.Anchors
            .Where(a => string.IsNullOrEmpty(filter) || a.Contains(filter!, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
         return new Dictionary<string, object?> { ["count"] = names.Count, ["tables"] = names };
      }

      public static object ReadTable(IDataModel model, string name, int start, int count) {
         var t = model.GetTableModel(name);
         if (t == null) return Err($"No table named '{name}'. Use list_tables to discover names.");
         var rows = ReadRows(t, start, count);
         return new Dictionary<string, object?> {
            ["name"] = name, ["total"] = t.Count, ["start"] = start,
            ["returned"] = rows.Count, ["fields"] = FieldNames(t), ["rows"] = rows };
      }

      public static object WriteValue(IDataModel model, Func<ModelDelta> token, string table, int index, string field, int value) {
         var t = model.GetTableModel(table, token);
         if (t == null) return Err($"No table named '{table}'.");
         if (index < 0 || index >= t.Count) return Err($"index {index} out of range (0..{t.Count - 1}).");
         var row = t[index];
         if (!row.HasField(field)) return Err($"No field '{field}' on table '{table}'.");
         int oldV = row.GetValue(field);
         row.SetValue(field, value);
         int newV = t[index].GetValue(field);
         return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["index"] = index, ["field"] = field, ["oldValue"] = oldV, ["newValue"] = newV };
      }

      public static object ExportToFile(IDataModel model, string name, string outPath) {
         var t = model.GetTableModel(name);
         if (t == null) return Err($"No table named '{name}'.");
         var rows = ReadRows(t, 0, t.Count);
         var payload = new Dictionary<string, object?> { ["name"] = name, ["total"] = t.Count, ["fields"] = FieldNames(t), ["rows"] = rows };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
         return new Dictionary<string, object?> { ["ok"] = true, ["name"] = name, ["rows"] = rows.Count, ["path"] = outPath };
      }

      public static Dictionary<string, object?> Err(string message) => new() { ["error"] = message };
   }
}
```

- [ ] **Step 2: Refactor `RomTools` to delegate to `RomAutomation`.** Replace the bodies of `ListTables`/`ReadTable`/`WriteValue`/`ExportTable` with calls to `RomAutomation.*` against `session.Require()` (writes pass `() => session.Token`), then `WrapHeadless(result)`. Add a helper that serializes the object and stamps `mode`:

```csharp
private static string WrapHeadless(object result) {
   var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(result))!.AsObject();
   node["mode"] = "headless";
   return node.ToJsonString();
}
```
`run_script` and `open_rom`/`save_rom` keep their current bodies for now (still call `RomAutomation` only where it already existed); just route their final serialization through a `mode`-stamping wrapper too.

- [ ] **Step 3: Build the MCP.** Run: `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` — Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 4: Run the headless gate.** Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN` (11/11). The tool outputs now also contain `"mode":"headless"`; the gate's assertions still pass (they check fields/values, not absence of `mode`).

- [ ] **Step 5: Commit.**
```bash
git add src/HexManiac.Core/RomAutomation.cs src/HexManiac.Mcp/RomTools.cs
git commit -m "Extract shared RomAutomation into Core; headless tools delegate to it"
```

---

## Task 2: GUI named-pipe automation server (read path)

**Files:**
- Create: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `src/HexManiac.WPF/Windows/App.xaml.cs` (start server after `MainWindow.Show()`)
- Test: manual pipe probe (PowerShell) + GUI launch

**Interfaces:**
- Consumes: `RomAutomation.*`, `AutoRequest`, `AutoResponse`, `EditorViewModel` (tabs), `ViewPort` (`Model`, `FullFileName`, `Name`, `CurrentChange`).
- Produces: `class AutomationPipeServer { AutomationPipeServer(EditorViewModel editor); void Start(); }`. Pipe methods: `list_tabs`, `read_table`, `list_tables`.

- [ ] **Step 1: Create `AutomationPipeServer.cs`.** Background loop accepting one persistent client; newline-delimited JSON (`AutoRequest` per line → `AutoResponse` per line). Each request runs inside `Application.Current.Dispatcher.Invoke(...)`. Tab resolution: `ResolveTab(int? index, string? file)` → default `editor.SelectedTab`; match `ViewPort` by index or `FullFileName`/`Name` substring.

```csharp
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;

namespace HavenSoft.HexManiac.WPF {
   public class AutomationPipeServer {
      public const string PipeName = "HexManiacAdvance.Automation";
      private readonly EditorViewModel editor;
      public AutomationPipeServer(EditorViewModel editor) => this.editor = editor;

      public void Start() => new Thread(Loop) { IsBackground = true, Name = "MCP-Automation" }.Start();

      private void Loop() {
         while (true) {
            try {
               using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                  PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
               server.WaitForConnection();
               using var reader = new StreamReader(server, Encoding.UTF8);
               using var writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };
               string? line;
               while ((line = reader.ReadLine()) != null) {
                  AutoResponse resp;
                  try {
                     var req = JsonSerializer.Deserialize<AutoRequest>(line)!;
                     resp = Application.Current.Dispatcher.Invoke(() => Handle(req));
                  } catch (System.Exception ex) {
                     resp = new AutoResponse(false, null, ex.Message);
                  }
                  writer.WriteLine(JsonSerializer.Serialize(resp));
               }
            } catch { /* client dropped; loop and re-listen */ }
         }
      }

      private AutoResponse Ok(object o) =>
         new(true, JsonSerializer.SerializeToElement(o), null);

      private ViewPort? ResolveTab(JsonElement p) {
         var tabs = new List<ViewPort>();
         foreach (var t in editor) if (t is ViewPort vp) tabs.Add(vp);
         if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("tab", out var tab) && tab.ValueKind != JsonValueKind.Null) {
            if (tab.ValueKind == JsonValueKind.Number) return tabs.ElementAtOrDefault(tab.GetInt32());
            if (tab.ValueKind == JsonValueKind.String) {
               var s = tab.GetString()!;
               return tabs.FirstOrDefault(v => (v.FullFileName ?? v.Name ?? "").Contains(s, System.StringComparison.OrdinalIgnoreCase));
            }
         }
         return editor.SelectedTab as ViewPort ?? tabs.FirstOrDefault();
      }

      private static string Str(JsonElement p, string key, string fallback = "") =>
         p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : fallback;
      private static int Int(JsonElement p, string key, int fallback) =>
         p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

      private AutoResponse Handle(AutoRequest req) {
         var p = req.Params;
         switch (req.Method) {
            case "list_tabs": {
               var list = new List<object>(); int i = 0;
               foreach (var t in editor) { if (t is ViewPort vp) list.Add(new { index = i, file = vp.FullFileName ?? vp.Name, selected = ReferenceEquals(t, editor.SelectedTab) }); i++; }
               return Ok(new { tabs = list });
            }
            case "list_tables": { var vp = ResolveTab(p); if (vp == null) return new(false, null, "No open ROM tab."); return Ok(RomAutomation.ListTables(vp.Model, Str(p, "filter", null!) is { Length: > 0 } f ? f : null)); }
            case "read_table": { var vp = ResolveTab(p); if (vp == null) return new(false, null, "No open ROM tab."); return Ok(RomAutomation.ReadTable(vp.Model, Str(p, "name"), Int(p, "start", 0), Int(p, "count", 25))); }
            default: return new(false, null, $"Unknown or not-yet-live method: {req.Method}");
         }
      }
   }
}
```

- [ ] **Step 2: Start the server in `App.OnStartup`.** After `MainWindow.Show();` (around `App.xaml.cs:70`), add:
```csharp
new AutomationPipeServer(viewModel).Start();
```
(`viewModel` is the `EditorViewModel` already in scope.)

- [ ] **Step 3: Build the GUI.** Run: `dotnet build -c Release -p:PlatformTarget=x64` (repo root) — Expected: `Build succeeded`.

- [ ] **Step 4: Launch GUI with the test ROM and probe the pipe.** Launch:
```powershell
Start-Process "artifacts\HexManiac.WPF\bin\Release\net6.0-windows\HexManiacAdvance.exe" "Q:\Users\user\Projects\HexManiacAdvance-MCP\test\roms\firered.gba"
```
Probe (PowerShell named-pipe client):
```powershell
$c = New-Object System.IO.Pipes.NamedPipeClientStream('.','HexManiacAdvance.Automation','InOut'); $c.Connect(3000)
$w = New-Object System.IO.StreamWriter($c); $w.AutoFlush=$true; $r = New-Object System.IO.StreamReader($c)
$w.WriteLine('{"Method":"list_tabs","Params":{}}'); $r.ReadLine()
$w.WriteLine('{"Method":"read_table","Params":{"name":"data.pokemon.stats","start":1,"count":1}}'); $r.ReadLine()
```
Expected: first line lists the firered tab; second returns Bulbasaur with `hp=45`.

- [ ] **Step 5: Commit.**
```bash
git add src/HexManiac.WPF/AutomationPipeServer.cs src/HexManiac.WPF/Windows/App.xaml.cs
git commit -m "GUI: named-pipe automation server (list_tabs/read_table/list_tables)"
```

---

## Task 3: MCP `GuiBridge` + live read routing + `list_open_roms` + `mode`

**Files:**
- Create: `src/HexManiac.Mcp/GuiBridge.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Test: `test/mcp-live-smoke.sh` (created in Task 5; until then, manual)

**Interfaces:**
- Produces: `static class GuiBridge { static bool TryCall(string method, object @params, out string resultJson, out string error); static bool IsGuiRunning(); }`. `TryCall` connects to the pipe (short timeout), sends one `AutoRequest`, returns the `AutoResponse.Result` JSON.

- [ ] **Step 1: Create `GuiBridge.cs`.** Connects with a 300 ms timeout; serializes `AutoRequest`; returns the result JSON string or error.

```csharp
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

public static class GuiBridge {
   private const string PipeName = "HexManiacAdvance.Automation";

   public static bool IsGuiRunning() {
      try { using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut); c.Connect(200); return true; }
      catch { return false; }
   }

   public static bool TryCall(string method, object @params, out string resultJson, out string error) {
      resultJson = ""; error = "";
      try {
         using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
         c.Connect(300);
         using var w = new StreamWriter(c, new UTF8Encoding(false)) { AutoFlush = true };
         using var r = new StreamReader(c, Encoding.UTF8);
         var req = new AutoRequest(method, JsonSerializer.SerializeToElement(@params));
         w.WriteLine(JsonSerializer.Serialize(req));
         var line = r.ReadLine();
         if (line == null) { error = "no response from GUI"; return false; }
         var resp = JsonSerializer.Deserialize<AutoResponse>(line)!;
         if (!resp.Ok) { error = resp.Error ?? "GUI error"; return false; }
         resultJson = resp.Result?.GetRawText() ?? "{}";
         return true;
      } catch (System.Exception ex) { error = ex.Message; return false; }
   }
}
```

- [ ] **Step 2: Add `list_open_roms` and live routing to `RomTools`.** Add a helper that prefers the GUI and stamps `mode`:
```csharp
private static string Dispatch(string method, object liveParams, System.Func<object> headless) {
   if (GuiBridge.TryCall(method, liveParams, out var json, out _))
      return Stamp(json, "live");
   return Stamp(JsonSerializer.Serialize(headless()), "headless");
}
private static string Stamp(string json, string mode) {
   var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
   node["mode"] = mode;
   return node.ToJsonString();
}
```
Route `list_tables`/`read_table`/`export_table` through `Dispatch` (live params include `tab`). Add the new tool:
```csharp
[McpServerTool(Name = "list_open_roms")]
[Description("List ROMs currently open: the running GUI's tabs if present, else the headless-loaded ROM.")]
public string ListOpenRoms(RomSession session) {
   if (GuiBridge.TryCall("list_tabs", new { }, out var json, out _)) return Stamp(json, "live");
   var open = session.Model != null
      ? new { tabs = new[] { new { index = 0, file = session.RomPath, selected = true } } }
      : new { tabs = System.Array.Empty<object>() };
   return Stamp(JsonSerializer.Serialize(open), "headless");
}
```
Add optional `int? tab = null` / `string? tabFile = null` params to `read_table`/`list_tables`/`export_table`; pass them in `liveParams`.

- [ ] **Step 3: Build MCP.** Run: `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` — Expected: 0 errors.

- [ ] **Step 4: Headless still green.** Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN` (GUI not running → `mode:"headless"`).

- [ ] **Step 5: Manual live read.** With the GUI open on firered (from Task 2), drive the MCP server (init → `list_open_roms` → `read_table`) and confirm responses carry `"mode":"live"` and the firered tab + Bulbasaur `hp=45`.

- [ ] **Step 6: Commit.**
```bash
git add src/HexManiac.Mcp/GuiBridge.cs src/HexManiac.Mcp/RomTools.cs
git commit -m "MCP: GuiBridge live routing, list_open_roms, mode field"
```

---

## Task 4: Live writes (write_value / save_rom / run_script)

**Files:**
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs` (add write methods)
- Modify: `src/HexManiac.Mcp/RomTools.cs` (route writes through `Dispatch`)
- Test: manual + live gate (Task 5)

**Interfaces:**
- Consumes: `ViewPort.CurrentChange`, `ViewPort.Save`, `RomAutomation.WriteValue`.

- [ ] **Step 1: Add write handlers to `AutomationPipeServer.Handle`.** All on the Dispatcher thread (already wrapped):
```csharp
case "write_value": {
   var vp = ResolveTab(p); if (vp == null) return new(false, null, "No open ROM tab.");
   return Ok(RomAutomation.WriteValue(vp.Model, () => vp.CurrentChange,
      Str(p, "table"), Int(p, "index", -1), Str(p, "field"), Int(p, "value", 0)));
}
case "export_table": { var vp = ResolveTab(p); if (vp == null) return new(false, null, "No open ROM tab."); return Ok(RomAutomation.ExportToFile(vp.Model, Str(p, "name"), Str(p, "outPath"))); }
case "run_script": {
   var vp = ResolveTab(p); if (vp == null) return new(false, null, "No open ROM tab.");
   var before = vp.Model.RawData.Length; vp.Edit(Str(p, "script")); vp.ChangeHistory.ChangeCompleted();
   return Ok(new { ok = true, ranOn = vp.FullFileName ?? vp.Name });
}
case "save_rom": {
   var vp = ResolveTab(p); if (vp == null) return new(false, null, "No open ROM tab.");
   vp.Save.Execute(((MainWindow)Application.Current.MainWindow).FileSystem);
   return Ok(new { ok = true, saved = vp.FullFileName ?? vp.Name });
}
```
(Live writes intentionally edit the open tab; no on-disk save unless `save_rom` is called — matching the GUI's own behavior.)

- [ ] **Step 2: Route `write_value`/`run_script`/`save_rom` through `Dispatch` in `RomTools`** (live params include `tab`), keeping headless fallbacks calling `RomAutomation` / `session`.

- [ ] **Step 3: Build GUI + MCP.** GUI: `dotnet build -c Release -p:PlatformTarget=x64`. MCP: `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`. Expected: both 0 errors.

- [ ] **Step 4: Manual live write check.** GUI open on firered; via MCP `write_value` set `data.pokemon.stats[1].hp=77`; confirm the GUI's pokemon stats table shows 77 (open that table in the GUI) and the response is `"mode":"live"`. Confirm Ctrl+Z in the GUI undoes it.

- [ ] **Step 5: Headless gate still green.** Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN`.

- [ ] **Step 6: Commit.**
```bash
git add src/HexManiac.WPF/AutomationPipeServer.cs src/HexManiac.Mcp/RomTools.cs
git commit -m "Live writes: write_value/run_script/save_rom via the GUI tab"
```

---

## Task 5: Live gate script + docs

**Files:**
- Create: `test/mcp-live-smoke.sh`
- Modify: `BUILD.md`

- [ ] **Step 1: Write `test/mcp-live-smoke.sh`.** Build both; launch the GUI with firered; wait; drive the MCP server (init → `list_open_roms` → `read_table` known value → `write_value` → `read_table` read-back) asserting every response has `"mode":"live"` and the write round-trips; kill the GUI at the end.

```bash
#!/usr/bin/env bash
set -uo pipefail
cd "$(dirname "$0")/.."
export PATH="/c/Program Files/dotnet:$PATH"
ROM="$(cygpath -m "$(pwd)/test/roms/firered.gba")"
GUI="artifacts/HexManiac.WPF/bin/Release/net6.0-windows/HexManiacAdvance.exe"
MCP="src/HexManiac.Mcp/artifacts/HexManiac.Mcp/bin/Release/net8.0/HexManiac.Mcp.exe"
TMP="test/.tmp"; mkdir -p "$TMP"; OUT="$TMP/live.jsonl"
PASS=0; FAIL=0; ok(){ echo "  PASS: $1"; PASS=$((PASS+1)); }; bad(){ echo "  FAIL: $1"; FAIL=$((FAIL+1)); }
taskkill //F //IM HexManiacAdvance.exe //IM HexManiac.Mcp.exe >/dev/null 2>&1 || true
( cd . && dotnet build -c Release -p:PlatformTarget=x64 ) >/dev/null 2>&1 || { echo "GUI build failed"; exit 1; }
( cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release ) >/dev/null 2>&1 || { echo "MCP build failed"; exit 1; }
powershell.exe -NoProfile -Command "Start-Process '$GUI' '$(cygpath -w "$(pwd)/test/roms/firered.gba")'" >/dev/null 2>&1
sleep 12
{
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"live","version":"1"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_open_roms","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":77}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
} | "./$MCP" > "$OUT" 2>/dev/null
rt(){ jq -rs --argjson id "$1" 'map(select(.id==$id))[0].result.content[0].text//empty' "$OUT"; }
[ "$(rt 2 | jq -r '.mode')" = "live" ] && ok "list_open_roms live" || bad "list_open_roms not live"
[ "$(rt 2 | jq -r '.tabs[0].file' | grep -ci firered)" -ge 1 ] && ok "firered tab listed" || bad "firered tab missing"
[ "$(rt 3 | jq -r '.rows[0].hp')" = "45" ] && ok "live read hp=45" || bad "live read"
[ "$(rt 4 | jq -r '.mode')" = "live" ] && ok "write_value live" || bad "write not live"
[ "$(rt 5 | jq -r '.rows[0].hp')" = "77" ] && ok "live write read-back hp=77" || bad "write not reflected"
taskkill //F //IM HexManiacAdvance.exe >/dev/null 2>&1 || true
echo "PASS=$PASS FAIL=$FAIL"; [ "$FAIL" -eq 0 ] && echo "LIVE GREEN" && exit 0 || exit 1
```

- [ ] **Step 2: Run the live gate.** Run: `bash test/mcp-live-smoke.sh` — Expected: `LIVE GREEN` (5/5).

- [ ] **Step 3: Run the headless gate.** Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN`.

- [ ] **Step 4: Update `BUILD.md`** with a "Live GUI mode" section: launch the GUI, then MCP tools auto-target the open tabs (`mode:"live"`); when the GUI is closed they fall back to headless; `bash test/mcp-live-smoke.sh` verifies live.

- [ ] **Step 5: Commit.**
```bash
git add test/mcp-live-smoke.sh BUILD.md
git commit -m "Add live-mode gate and docs"
```

---

## Self-Review notes

- **Spec coverage:** architecture/two-backends (T1,T3), RomAutomation-in-Core (T1), pipe server + UI-thread marshaling (T2), GuiBridge + mode field (T3), list_open_roms + tab arg (T3), live writes via CurrentChange (T4), headless preserved (gate run every task), live gate (T5). All covered.
- **Headless invariant:** `mcp-smoke.sh` is run in T1, T3, T4, T5.
- **Type consistency:** `AutoRequest(Method,Params)` / `AutoResponse(Ok,Result,Error)` used identically in server (T2) and client (T3); `RomAutomation` signatures match call sites.
