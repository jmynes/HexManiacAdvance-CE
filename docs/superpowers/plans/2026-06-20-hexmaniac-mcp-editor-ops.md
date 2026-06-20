# MCP Editor Operations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add MCP tools for the editor operations a person uses in the HexManiacAdvance GUI: undo/redo, table selection, self-contained row copy/paste, and GUI-clipboard parity.

**Architecture:** Each operation is one MCP tool routed through the existing `Dispatch` helper (live → GUI automation pipe, else headless), with a `mode` field on every result. View-independent logic (row byte-range, copy bytes, paste bytes) lives in `HexManiac.Core/RomAutomation` and is shared by both backends; selection and clipboard need the `ViewPort` and live only in the pipe handler.

**Tech Stack:** C# / .NET (Core net6.0, WPF net6.0, MCP net8.0), `System.Text.Json`, ModelContextProtocol 1.4.0, xUnit (HexManiac.Tests), bash + jq gates.

## Global Constraints

- Headless must keep working unchanged; `test/mcp-smoke.sh` stays ALL GREEN every task.
- GUI model access ONLY on the WPF Dispatcher thread (the pipe `Handle` already runs inside `Application.Current.Dispatcher.Invoke`; handlers add no extra dispatching).
- Live edits go through the tab's change token (`vp.CurrentChange`) so they are visible + undoable; `RomSession.Token` is the headless ViewPort's `CurrentChange`, so headless `undo` covers headless `write_value`.
- Every MCP tool result includes `"mode": "live" | "headless"` via the existing `Dispatch`/`Stamp`. Live errors surface as `mode:"live"`.
- `select`, `clipboard_copy`, `clipboard_paste` are live-only; in headless they return a clear error (no view), never a crash or silent no-op.
- Errors return `RomAutomation.Err`-style `{ "error": ... }`; "nothing to undo/redo" is `{ ok, applied: 0 }`, not an error.
- Build: GUI = `dotnet build -c Release -p:PlatformTarget=x64` (repo root, SDK 6); MCP = `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` (SDK 8). Run the headless gate with NO HexManiacAdvance GUI running (`taskkill //F //IM HexManiacAdvance.exe` first); if a build hits a lock, the running MCP server/GUI must be killed (`taskkill //F //IM HexManiac.Mcp.exe`, `//IM HexManiacAdvance.exe`).
- Existing reusable helpers — do NOT reinvent: `RomTools.Dispatch(method, liveParams, tab, tabFile, headless)`, `RomTools.Stamp`, `RomTools.JsonToValue`; `AutomationPipeServer` `ResolveTab`, `Ok`, `NoTab`, `Str`, `StrOrNull`, `Int`, `Val`, `GuiFileSystem()`. The MCP tool count assertion in `test/mcp-smoke.sh` is currently `10`; each task that adds tools bumps it.

---

## File Structure

- **Modify `src/HexManiac.Core/RomAutomation.cs`** — add `RowRange`, `CopyRows`, `PasteRows` (pure, view-independent). One place for the byte logic.
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — seven new tools + a private static last-copy cache.
- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — seven new pipe cases.
- **Create `src/HexManiac.Tests/RomAutomationEditTests.cs`** — Core unit tests.
- **Modify `test/mcp-smoke.sh`** — headless assertions (undo, copy/paste round-trip, select live-only error).
- **Modify `test/mcp-live-smoke.sh`** — live assertions (select, clipboard round-trip, undo/redo).
- **Modify `BUILD.md`** — document the editor-ops tools.

---

## Task 1: Core row helpers (`RowRange`, `CopyRows`, `PasteRows`) + unit tests

**Files:**
- Modify: `src/HexManiac.Core/RomAutomation.cs`
- Create: `src/HexManiac.Tests/RomAutomationEditTests.cs`

**Interfaces:**
- Consumes: `IDataModel.GetTableModel(string, Func<ModelDelta>)`, `IDataModel.RawData` (`byte[]`); `ModelArrayElement.Start` (`int`), `.Length` (`int`); `ModelDelta.ChangeData(IDataModel, int, IReadOnlyList<byte>)`; `RomAutomation.Err`.
- Produces:
  - `static object RowRange(IDataModel model, string table, int index, int count)` → `{ ok, table, index, count, start, length }` or `{ error }`.
  - `static object CopyRows(IDataModel model, string table, int index, int count)` → `{ ok, table, index, count, bytes, elementLength }` (`bytes` = uppercase hex string) or `{ error }`.
  - `static object PasteRows(IDataModel model, Func<ModelDelta> token, string table, int index, string hex)` → `{ ok, table, index, count }` or `{ error }`.

- [ ] **Step 1: Write the failing tests.** Create `src/HexManiac.Tests/RomAutomationEditTests.cs`:

```csharp
using System.Collections.Generic;
using HavenSoft.HexManiac.Core.Models;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class RomAutomationEditTests : BaseViewModelTestClass {
      // A table of 3 two-byte elements at 0x40: [value: a 2-byte integer]3
      private void Setup() {
         ViewPort.Goto.Execute("40");
         ViewPort.Edit("^data[value:]3 11 22 33 ");  // elements: 0x0011, 0x0022, 0x0033
      }

      [Fact] public void RowRange_ComputesAddressAndLength() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.RowRange(Model, "data", 1, 2);
         Assert.Equal(0x42, r["start"]);   // 0x40 + 1*2
         Assert.Equal(4, r["length"]);     // 2 elements * 2 bytes
      }

      [Fact] public void CopyRows_ReturnsHexBytes() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.CopyRows(Model, "data", 2, 1);
         Assert.Equal(true, r["ok"]);
         Assert.Equal("3300", r["bytes"]); // element 2 = 0x0033, little-endian bytes 33 00
      }

      [Fact] public void PasteRows_ClonesElement() {
         Setup();
         var copy = (Dictionary<string, object?>)RomAutomation.CopyRows(Model, "data", 0, 1); // 0x0011 -> "1100"
         var paste = (Dictionary<string, object?>)RomAutomation.PasteRows(Model, () => Token, "data", 2, (string)copy["bytes"]);
         Assert.Equal(true, paste["ok"]);
         var read = (Dictionary<string, object?>)RomAutomation.ReadTable(Model, "data", 2, 1);
         var rows = (List<Dictionary<string, object?>>)read["rows"];
         Assert.Equal(0x11, rows[0]["value"]); // element 2 now equals element 0
      }

      [Fact] public void PasteRows_SizeMismatchReturnsError() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.PasteRows(Model, () => Token, "data", 0, "AABBCC"); // 3 bytes, elem len 2
         Assert.True(r.ContainsKey("error"));
      }

      [Fact] public void RowRange_OutOfRangeReturnsError() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.RowRange(Model, "data", 2, 5);
         Assert.True(r.ContainsKey("error"));
      }
   }
}
```

- [ ] **Step 2: Run the tests to verify they fail.** Run: `(cd src/HexManiac.Tests && dotnet test --filter "FullyQualifiedName~RomAutomationEditTests")` — Expected: compile error / FAIL (`RowRange`/`CopyRows`/`PasteRows` don't exist).

- [ ] **Step 3: Implement the helpers.** In `src/HexManiac.Core/RomAutomation.cs`, add (near `WriteValue`):

```csharp
public static object RowRange(IDataModel model, string table, int index, int count) {
   var t = model.GetTableModel(table);
   if (t == null) return Err($"No table named '{table}'.");
   if (count < 1) return Err("count must be at least 1.");
   if (index < 0 || index + count > t.Count) return Err($"rows {index}..{index + count - 1} out of range (0..{t.Count - 1}).");
   var first = t[index];
   int length = 0;
   for (int i = 0; i < count; i++) length += t[index + i].Length;
   return new Dictionary<string, object?> {
      ["ok"] = true, ["table"] = table, ["index"] = index, ["count"] = count,
      ["start"] = first.Start, ["length"] = length,
   };
}

public static object CopyRows(IDataModel model, string table, int index, int count) {
   var range = RowRange(model, table, index, count);
   if (range is Dictionary<string, object?> e && e.ContainsKey("error")) return range;
   var r = (Dictionary<string, object?>)range;
   int start = (int)r["start"]!, length = (int)r["length"]!;
   var sb = new System.Text.StringBuilder(length * 2);
   for (int i = 0; i < length; i++) sb.Append(model.RawData[start + i].ToString("X2"));
   return new Dictionary<string, object?> {
      ["ok"] = true, ["table"] = table, ["index"] = index, ["count"] = count,
      ["bytes"] = sb.ToString(), ["elementLength"] = length / count,
   };
}

public static object PasteRows(IDataModel model, Func<ModelDelta> token, string table, int index, string hex) {
   if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return Err("paste data must be a non-empty hex string with an even length.");
   var bytes = new byte[hex.Length / 2];
   for (int i = 0; i < bytes.Length; i++) {
      if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
         return Err($"paste data is not valid hex at byte {i}.");
   }
   var t = model.GetTableModel(table, token);
   if (t == null) return Err($"No table named '{table}'.");
   if (index < 0 || index >= t.Count) return Err($"index {index} out of range (0..{t.Count - 1}).");
   int elementLength = t[index].Length;
   if (bytes.Length % elementLength != 0) return Err($"paste size {bytes.Length} is not a multiple of the element length {elementLength}.");
   int count = bytes.Length / elementLength;
   if (index + count > t.Count) return Err($"pasting {count} rows at {index} exceeds the table (0..{t.Count - 1}).");
   token().ChangeData(model, t[index].Start, bytes);
   return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["index"] = index, ["count"] = count };
}
```

- [ ] **Step 4: Run the tests to verify they pass.** Run: `(cd src/HexManiac.Tests && dotnet test --filter "FullyQualifiedName~RomAutomationEditTests")` — Expected: 5/5 pass. (If `ChangeData` expects `IReadOnlyList<byte>` and rejects `byte[]`, wrap as `bytes.ToList()` — `byte[]` implements `IReadOnlyList<byte>`, so it should bind directly.)

- [ ] **Step 5: Build MCP + GUI to confirm Core still compiles for both.** Run: `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` then `dotnet build -c Release -p:PlatformTarget=x64`. Expected: both 0 errors.

- [ ] **Step 6: Commit.**
```bash
git add src/HexManiac.Core/RomAutomation.cs src/HexManiac.Tests/RomAutomationEditTests.cs
git commit -m "Core: RowRange/CopyRows/PasteRows helpers + tests"
```

---

## Task 2: `undo` / `redo` tools (live + headless)

**Files:**
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `test/mcp-smoke.sh`

**Interfaces:**
- Consumes: `ViewPort.Undo`/`Redo` (`ICommand`, `CanExecute(null)`/`Execute(null)`); `ViewPort.ChangeHistory.ChangeCompleted()`; `RomSession.RequireViewPort()`.
- Produces: MCP tools `undo(count=1, tab?, tabFile?)` and `redo(count=1, tab?, tabFile?)`; pipe methods `undo`, `redo`. Result `{ ok, applied, mode }`.

- [ ] **Step 1: Write the failing test — extend `test/mcp-smoke.sh`.** Bump the tool-count assertion from `10` to `12`. Add to the driver block after the typed-write block (after id:23):

```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":123}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":25,"method":"tools/call","params":{"name":"undo","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":26,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
```

Add assertions:

```bash
[ "$(result_text 25 | jq -r '.applied' 2>/dev/null)" -ge 1 ] && ok "undo applied >=1" || bad "undo applied"
[ "$(result_text 26 | jq -r '.rows[0].hp' 2>/dev/null)" != "123" ] && ok "undo reverted hp write" || bad "undo did not revert"
```

Bump the count assertion line:
```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "12" ] && ok "tools/list shows 12 tools" || bad "tools/list"
```

- [ ] **Step 2: Run the gate to verify it fails.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: FAIL (`undo` is an unknown tool; tools count is 10, not 12).

- [ ] **Step 3: Add the pipe handlers.** In `src/HexManiac.WPF/AutomationPipeServer.cs`, add before `default:`:

```csharp
            case "undo": return Ok(ApplyHistory(ResolveTab(p), Int(p, "count", 1), redo: false));
            case "redo": return Ok(ApplyHistory(ResolveTab(p), Int(p, "count", 1), redo: true));
```

And a helper next to the other private helpers:

```csharp
      private static object ApplyHistory(ViewPort vp, int count, bool redo) {
         if (vp == null) return new { error = "No open ROM tab in the GUI." };
         vp.ChangeHistory.ChangeCompleted();            // commit any in-progress edit so it is on the stack
         var cmd = redo ? vp.Redo : vp.Undo;
         int applied = 0;
         while (applied < count && cmd.CanExecute(null)) { cmd.Execute(null); applied++; }
         return new { ok = true, applied };
      }
```

(`Ok(...)` wraps the object as an `AutoResponse`; returning the `{ error = ... }` object inside `Ok` is acceptable — it stamps as a result the MCP layer surfaces, consistent with `RomAutomation.Err`. If you prefer, return `NoTab()` when `vp == null`; either keeps behavior correct.)

- [ ] **Step 4: Add the MCP tools.** In `src/HexManiac.Mcp/RomTools.cs`, after `WriteValue`:

```csharp
   [McpServerTool(Name = "undo")]
   [Description("Undo up to 'count' edits on the active tab's change history (same stack as Ctrl+Z). Live GUI when present; else headless.")]
   public string Undo(RomSession session, [Description("How many steps to undo")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["count"] = count };
      return Dispatch("undo", p, tab, tabFile, () => HistoryHeadless(session, count, redo: false));
   }

   [McpServerTool(Name = "redo")]
   [Description("Redo up to 'count' edits on the active tab's change history (same stack as Ctrl+Y). Live GUI when present; else headless.")]
   public string Redo(RomSession session, [Description("How many steps to redo")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["count"] = count };
      return Dispatch("redo", p, tab, tabFile, () => HistoryHeadless(session, count, redo: true));
   }

   private static object HistoryHeadless(RomSession session, int count, bool redo) {
      var vp = session.RequireViewPort();
      vp.ChangeHistory.ChangeCompleted();
      var cmd = redo ? vp.Redo : vp.Undo;
      int applied = 0;
      while (applied < count && cmd.CanExecute(null)) { cmd.Execute(null); applied++; }
      return new { ok = true, applied };
   }
```

- [ ] **Step 5: Build MCP + GUI.** `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` and `dotnet build -c Release -p:PlatformTarget=x64`. Expected: 0 errors.

- [ ] **Step 6: Run the gate to verify it passes.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: `ALL GREEN`, including `tools/list shows 12 tools`, `undo applied >=1`, `undo reverted hp write`. (If `undo reverted` fails — hp still 123 — headless undo isn't reverting the write; the change likely wasn't committed before undo. Confirm `ApplyHistory`/`HistoryHeadless` call `ChangeCompleted()` before `Undo`; if it still fails, the write and the undo target different histories — make headless `write_value` and `undo` use the same `RequireViewPort().CurrentChange`/`ChangeHistory` and re-run. Do not weaken the assertion.)

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-smoke.sh
git commit -m "MCP undo/redo tools (live + headless)"
```

---

## Task 3: `copy_rows` / `paste_rows` tools (live + headless)

**Files:**
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `test/mcp-smoke.sh`

**Interfaces:**
- Consumes: `RomAutomation.CopyRows(model, table, index, count)`, `RomAutomation.PasteRows(model, token, table, index, hex)` (Task 1); `Str`/`Int`/`StrOrNull`/`Val` and `Dispatch`.
- Produces: MCP tools `copy_rows(table, index, count=1, tab?, tabFile?)` and `paste_rows(table, index, data=null, tab?, tabFile?)`; pipe methods `copy_rows`, `paste_rows`. A private static `RomTools` field caching the last copied hex.

- [ ] **Step 1: Write the failing test — extend `test/mcp-smoke.sh`.** Bump tool count `12`→`14`. After id:26 add:

```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":27,"method":"tools/call","params":{"name":"copy_rows","arguments":{"table":"data.pokemon.stats","index":1,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":28,"method":"tools/call","params":{"name":"paste_rows","arguments":{"table":"data.pokemon.stats","index":4}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":29,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":4,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
```

Add assertions (paste of row 1 onto row 4 makes row 4's hp equal row 1's hp):

```bash
[ -n "$(result_text 27 | jq -r '.bytes // empty' 2>/dev/null)" ] && ok "copy_rows returns bytes" || bad "copy_rows bytes"
[ "$(result_text 28 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "paste_rows ok" || bad "paste_rows"
[ "$(result_text 29 | jq -r '.rows[0].hp' 2>/dev/null)" = "$(result_text 30 | jq -r '.rows[0].hp' 2>/dev/null)" ] && ok "paste cloned row (hp matches)" || bad "paste clone"
```

Bump count line to `14`.

- [ ] **Step 2: Run the gate to verify it fails.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: FAIL (`copy_rows`/`paste_rows` unknown; count 12≠14).

- [ ] **Step 3: Add the pipe handlers.** In `AutomationPipeServer.cs`, before `default:`:

```csharp
            case "copy_rows": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               return Ok(RomAutomation.CopyRows(vp.Model, Str(p, "table"), Int(p, "index", -1), Int(p, "count", 1)));
            }
            case "paste_rows": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               return Ok(RomAutomation.PasteRows(vp.Model, () => vp.CurrentChange, Str(p, "table"), Int(p, "index", -1), Str(p, "data")));
            }
```

- [ ] **Step 4: Add the MCP tools + last-copy cache.** In `RomTools.cs`:

```csharp
   private static string _lastCopiedHex = "";

   [McpServerTool(Name = "copy_rows")]
   [Description("Copy 'count' table rows starting at 'index' as a hex byte string (cached for paste_rows). Live GUI when present; else headless.")]
   public string CopyRows(RomSession session, [Description("Anchor/table name")] string table,
      [Description("First row index")] int index, [Description("How many rows")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["count"] = count };
      var json = Dispatch("copy_rows", p, tab, tabFile, () => RomAutomation.CopyRows(session.Require(), table, index, count));
      try { var hex = System.Text.Json.Nodes.JsonNode.Parse(json)?["bytes"]?.GetValue<string>(); if (!string.IsNullOrEmpty(hex)) _lastCopiedHex = hex; } catch { }
      return json;
   }

   [McpServerTool(Name = "paste_rows")]
   [Description("Paste row bytes onto the table starting at 'index' (undoable). 'data' is hex; if omitted, uses the last copy_rows result. Live GUI when present; else headless.")]
   public string PasteRows(RomSession session, [Description("Anchor/table name")] string table,
      [Description("Destination row index")] int index, [Description("Hex bytes to paste (default: last copy_rows)")] string? data = null,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var hex = string.IsNullOrEmpty(data) ? _lastCopiedHex : data;
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["data"] = hex };
      return Dispatch("paste_rows", p, tab, tabFile, () => RomAutomation.PasteRows(session.Require(), () => session.Token, table, index, hex));
   }
```

(`Dispatch` returns the stamped JSON string; reading `bytes` back from it to refresh the cache works for both live and headless results.)

- [ ] **Step 5: Build MCP + GUI.** Both build commands → 0 errors.

- [ ] **Step 6: Run the gate to verify it passes.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: `ALL GREEN` incl. `copy_rows returns bytes`, `paste_rows ok`, `paste cloned row (hp matches)`, `tools/list shows 14 tools`.

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-smoke.sh
git commit -m "MCP copy_rows/paste_rows tools (live + headless)"
```

---

## Task 4: `select` tool (live-only)

**Files:**
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `test/mcp-smoke.sh` (headless live-only error), `test/mcp-live-smoke.sh` (live selection)

**Interfaces:**
- Consumes: `RomAutomation.RowRange` (Task 1); `ViewPort.SelectionStart`/`SelectionEnd` (`Point`, set), `ViewPort.ConvertAddressToViewPoint(int)`; `ModelTable.Count`.
- Produces: MCP tool `select(table, index=null, count=1, tab?, tabFile?)`; pipe method `select`. Result `{ ok, table, index, count, start, length, mode }` (live) or `{ error }` (headless).

- [ ] **Step 1: Write the failing tests.** In `test/mcp-smoke.sh`, bump tool count `14`→`15`; after id:30 add:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"select","arguments":{"table":"data.pokemon.stats","index":1,"count":2}}}'; sleep 1
```
Assertion (headless = live-only error):
```bash
[ "$(result_text 31 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "select live-only in headless" || bad "select headless error"
```
Bump count line to `15`.

In `test/mcp-live-smoke.sh`, after the last existing driver line add:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"select","arguments":{"table":"data.pokemon.stats","index":1,"count":2}}}'; sleep 1
```
Assertions:
```bash
[ "$(rt 11 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "select mode=live" || bad "select not live"
[ "$(rt 11 | jq -r '.count' 2>/dev/null)" = "2" ] && ok "select count=2" || bad "select count"
```

- [ ] **Step 2: Run both gates to verify failure.** `bash test/mcp-smoke.sh` (no GUI) FAILs (`select` unknown; count 14≠15). `bash test/mcp-live-smoke.sh` FAILs (select unknown → headless fallback error, not `mode:live`).

- [ ] **Step 3: Add the pipe handler.** In `AutomationPipeServer.cs`, before `default:`:

```csharp
            case "select": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var t = vp.Model.GetTableModel(Str(p, "table"));
               if (t == null) return new AutoResponse(false, null, $"No table named '{Str(p, "table")}'.");
               int index = Int(p, "index", 0), count = Int(p, "count", 1);
               // index omitted (null) => whole table
               if (p.ValueKind == JsonValueKind.Object && (!p.TryGetProperty("index", out var iv) || iv.ValueKind == JsonValueKind.Null)) { index = 0; count = t.Count; }
               if (index < 0 || count < 1 || index + count > t.Count) return new AutoResponse(false, null, $"rows {index}..{index + count - 1} out of range (0..{t.Count - 1}).");
               int start = t[index].Start, last = t[index + count - 1].Start + t[index + count - 1].Length - 1;
               vp.SelectionStart = vp.ConvertAddressToViewPoint(start);
               vp.SelectionEnd = vp.ConvertAddressToViewPoint(last);
               return Ok(new { ok = true, table = Str(p, "table"), index, count, start, length = last - start + 1 });
            }
```

- [ ] **Step 4: Add the MCP tool.** In `RomTools.cs`:

```csharp
   [McpServerTool(Name = "select")]
   [Description("Select rows in the live GUI: 'count' rows from 'index', or the whole table if 'index' is omitted. Live GUI only.")]
   public string Select(RomSession session, [Description("Anchor/table name")] string table,
      [Description("First row index (omit for the whole table)")] int? index = null, [Description("How many rows")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["table"] = table, ["count"] = count };
      if (index.HasValue) p["index"] = index.Value;
      return Dispatch("select", p, tab, tabFile, () => RomAutomation.Err("select requires the live GUI (no view to select in headless mode)."));
   }
```

- [ ] **Step 5: Build GUI + MCP.** Both → 0 errors.

- [ ] **Step 6: Run both gates.** `bash test/mcp-smoke.sh` (no GUI) → `ALL GREEN` (incl. `select live-only in headless`, `tools/list shows 15 tools`). `bash test/mcp-live-smoke.sh` → `LIVE GREEN` (incl. `select mode=live`, `select count=2`).

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-smoke.sh test/mcp-live-smoke.sh
git commit -m "MCP select tool (live-only row/table selection)"
```

---

## Task 5: `clipboard_copy` / `clipboard_paste` tools (live-only, Phase 2)

**Files:**
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `test/mcp-smoke.sh` (count + live-only errors), `test/mcp-live-smoke.sh` (clipboard round-trip)

**Interfaces:**
- Consumes: `ViewPort.Copy` (`ICommand`, parameter `IFileSystem`); `IFileSystem.CopyText` (get/set); `ViewPort.Edit(string)`; `ViewPort.ChangeHistory.ChangeCompleted()`; `GuiFileSystem()`.
- Produces: MCP tools `clipboard_copy(tab?, tabFile?)`, `clipboard_paste(tab?, tabFile?)`; pipe methods `clipboard_copy`, `clipboard_paste`. Results `{ ok, text, mode }` and `{ ok, mode }`.

- [ ] **Step 1: Write the failing tests.** In `test/mcp-smoke.sh`, bump tool count `15`→`17`; after id:31 add:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":32,"method":"tools/call","params":{"name":"clipboard_copy","arguments":{}}}'; sleep 1
```
Assertion (headless live-only error):
```bash
[ "$(result_text 32 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "clipboard_copy live-only in headless" || bad "clipboard headless error"
```
Bump count line to `17`.

In `test/mcp-live-smoke.sh`, after id:11 add (select a row, copy it, paste onto another, read back):
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"clipboard_copy","arguments":{}}}'; sleep 1
```
Assertion:
```bash
[ "$(rt 12 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "clipboard_copy mode=live" || bad "clipboard_copy not live"
[ -n "$(rt 12 | jq -r '.text // empty' 2>/dev/null)" ] && ok "clipboard_copy returns text" || bad "clipboard_copy text"
```
(id:11 selected rows 1..2, so id:12 copies a non-empty selection.)

- [ ] **Step 2: Run both gates to verify failure.** Headless FAILs (`clipboard_copy` unknown; count 15≠17). Live FAILs (clipboard_copy → headless fallback, not `mode:live`, no `text`).

- [ ] **Step 3: Add the pipe handlers.** In `AutomationPipeServer.cs`, before `default:`:

```csharp
            case "clipboard_copy": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var fs = GuiFileSystem();
               vp.Copy.Execute(fs);
               return Ok(new { ok = true, text = fs.CopyText ?? "" });
            }
            case "clipboard_paste": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var text = GuiFileSystem().CopyText ?? "";
               if (text.Length > 0) { vp.Edit(text); vp.ChangeHistory.ChangeCompleted(); }
               return Ok(new { ok = true, pasted = text.Length });
            }
```

(This mirrors `EditorViewModel.paste.Execute`: read `fileSystem.CopyText` and `viewPort.Edit(copyText)`. Keep it minimal; the GUI's extra whitespace normalization is not required for parity here, but if a live paste round-trip mis-completes the last element, add the same trailing-space normalization `EditorViewModel.paste` uses.)

- [ ] **Step 4: Add the MCP tools.** In `RomTools.cs`:

```csharp
   [McpServerTool(Name = "clipboard_copy")]
   [Description("Copy the live GUI's current selection to the system clipboard; returns the copied text. Live GUI only.")]
   public string ClipboardCopy(RomSession session, [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("clipboard_copy", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("clipboard_copy requires the live GUI."));
   }

   [McpServerTool(Name = "clipboard_paste")]
   [Description("Paste the system clipboard at the live GUI's current selection (undoable). Live GUI only.")]
   public string ClipboardPaste(RomSession session, [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("clipboard_paste", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("clipboard_paste requires the live GUI."));
   }
```

- [ ] **Step 5: Build GUI + MCP.** Both → 0 errors.

- [ ] **Step 6: Run both gates.** `bash test/mcp-smoke.sh` (no GUI) → `ALL GREEN` (incl. `clipboard_copy live-only in headless`, `tools/list shows 17 tools`). `bash test/mcp-live-smoke.sh` → `LIVE GREEN` (incl. `clipboard_copy mode=live`, `clipboard_copy returns text`).

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-smoke.sh test/mcp-live-smoke.sh
git commit -m "MCP clipboard_copy/clipboard_paste tools (live-only GUI clipboard parity)"
```

---

## Task 6: Docs

**Files:**
- Modify: `BUILD.md`

- [ ] **Step 1: Document the editor-ops tools.** In `BUILD.md`, add a subsection near the other tool docs:

```markdown
### Editor operations

- `undo` / `redo` — apply up to `count` steps on the active tab's change history
  (same stack as Ctrl+Z/Y). Live + headless. Returns how many steps applied.
- `select` — select `count` rows from `index`, or the whole table if `index` is
  omitted; scrolls into view. **Live only.**
- `copy_rows` / `paste_rows` — copy `count` rows to a hex string (cached), then
  paste onto another index (undoable). Clones rows; live + headless.
- `clipboard_copy` / `clipboard_paste` — drive the GUI's real Copy/Paste over the
  current selection, sharing the system clipboard with manual Ctrl+C/V. **Live only.**
```

- [ ] **Step 2: Commit.**
```bash
git add BUILD.md
git commit -m "Docs: editor-ops tools in BUILD.md"
```

---

## Self-Review notes

- **Spec coverage:** history (T2), selection incl. whole-table when index omitted (T4), self-contained copy/paste (T1 Core + T3 tools), clipboard parity (T5), shared Core logic (T1), live/headless wiring + `mode` (every tool task), errors incl. live-only + paste size mismatch (T1/T4/T5), Core unit + headless + live gates (T1/T2/T3/T4/T5), docs (T6), phasing 1–3 then 4 (T2–T4 before T5). All covered.
- **Headless invariant:** `test/mcp-smoke.sh` run in T2, T3, T4, T5 (and T1 builds both SDKs).
- **Type consistency:** `RomAutomation.RowRange/CopyRows/PasteRows` signatures defined in T1, consumed identically in T3 (tools) and the pipe (T3); `ApplyHistory`/`HistoryHeadless` share the `{ ok, applied }` shape (T2); `select` result keys match between pipe (T4) and the documented shape; `_lastCopiedHex` cache defined and used only in T3. Tool count steps 10→12 (T2) →14 (T3) →15 (T4) →17 (T5), matching the seven added tools.
- **Undo caveat:** `RomSession.Token` is the headless ViewPort's `CurrentChange`, so headless `undo` should revert headless writes; T2 Step 6 asserts this and gives the remediation if the histories differ.
