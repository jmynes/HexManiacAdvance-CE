# Typed `write_value` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the MCP `write_value` tool to set strings, enum-by-name, and individual bit-array flags — not just integers — by routing through HexManiac.Core's existing type-aware element setter.

**Architecture:** One shared engine, `RomAutomation.WriteValue` (Core), gains an `object value` and optional `flag` and dispatches on the field's segment type via `ModelArrayElement`'s unified `element[field] = value` setter (PCS/pointer-text → string, enum → name or index, integer → number) plus a nested `ModelTupleElement[flag] = value` for bit-array checkboxes. The MCP tool widens `value` to a JSON value; the GUI pipe handler does the same. Both backends call the one engine.

**Tech Stack:** C# / .NET (Core net6.0, WPF net6.0, MCP net8.0), `System.Text.Json`, ModelContextProtocol 1.4.0, xUnit (HexManiac.Tests).

## Global Constraints

- Headless must keep working unchanged; `test/mcp-smoke.sh` stays ALL GREEN every task.
- GUI model access ONLY on the WPF Dispatcher thread (the pipe `Handle` already runs inside `Application.Current.Dispatcher.Invoke`).
- Live writes go through the tab's `CurrentChange` token (so GUI undo works) — already true for `write_value`.
- Every MCP tool result includes `"mode": "live" | "headless"` (via the existing `Dispatch`/`Stamp` helpers). Live errors surface as `mode:"live"`.
- `value` interpretation is decided by the field's segment type, never the caller.
- Setting a whole bit-array as one integer is NOT supported; named flags via `flag` are the path. Plain Integer fields still take a number exactly as before (backward compatible).
- Build commands: GUI = `dotnet build -c Release -p:PlatformTarget=x64` (repo root, SDK 6); MCP = `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` (SDK 8). The headless gate is only deterministic with NO HexManiacAdvance GUI running (`taskkill //F //IM HexManiacAdvance.exe` first).

---

## File Structure

- **Modify `src/HexManiac.Core/RomAutomation.cs`** — `WriteValue` becomes typed (`object value, string flag = null`) with validation; add private helpers. The one place the typed-write logic lives.
- **Create `src/HexManiac.Tests/RomAutomationWriteTests.cs`** — Core unit tests for the engine (no GUI).
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — `WriteValue` tool: `value` → `JsonElement`, add `flag`; coerce + route live/headless.
- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — `write_value` case reads a typed value + `flag`; add a `Val` reader helper.
- **Modify `test/mcp-smoke.sh`** — headless assertions for string + enum-by-name writes and a bad-value error.
- **Modify `test/mcp-live-smoke.sh`** — live assertions for a string write and a flag toggle.
- **Modify `BUILD.md`** — document the extended `write_value`.

---

## Task 1: Core typed engine + validation + unit tests

**Files:**
- Modify: `src/HexManiac.Core/RomAutomation.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs` (call-site compile fix only)
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs` (call-site compile fix only)
- Create: `src/HexManiac.Tests/RomAutomationWriteTests.cs`

**Interfaces:**
- Consumes: `IDataModel.GetTableModel(string, Func<ModelDelta>)`; `ModelArrayElement` members `HasField`, `Table` (`ITableRun`), `this[string]` get/set, `GetStringValue`, `GetValue`; `ModelTupleElement` `HasField`, `this[string]` get/set; segment types `ArrayRunElementSegment` (`.Name`,`.Type`), `ArrayRunEnumSegment` (`TryParse(model,text,out int)`, `GetOptions(model)`), `ArrayRunBitArraySegment` (`GetOptions(model)`), `ArrayRunTupleSegment` (`.Elements` of `.Name`), `ElementContentType` (`PCS`,`Pointer`,`Integer`). All in `HavenSoft.HexManiac.Core.Models[.Runs]`, already imported by RomAutomation.cs.
- Produces: `RomAutomation.WriteValue(IDataModel model, Func<ModelDelta> token, string table, int index, string field, object value, string flag = null) -> object` (a `Dictionary<string,object?>`: success `{ ok, table, index, field, [flag], oldValue, newValue }`, or `{ error }`).

- [ ] **Step 1: Write the failing unit tests.** Create `src/HexManiac.Tests/RomAutomationWriteTests.cs`:

```csharp
using System.Collections.Generic;
using HavenSoft.HexManiac.Core.Models;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class RomAutomationWriteTests : BaseViewModelTestClass {
      // Build: an enum option source `types`, then a `data` table with a PCS name,
      // an enum `kind` (-> types), an integer `power`, and a tuple-of-bits `flags` (a,b).
      private void Setup() {
         CreateTextTable("types", 0x00, "NORMAL", "FIGHTING", "FLYING");
         ViewPort.Goto.Execute("40");
         ViewPort.Edit("^data[name\"\"8 kind.types power. flags:|t|a:|b:]2 ");
      }
      private static Dictionary<string, object?> Write(BaseViewModelTestClass t, string field, object value, string flag = null)
         => (Dictionary<string, object?>)RomAutomation.WriteValue(t.Model, () => t.Token, "data", 0, field, value, flag);

      [Fact] public void SetsPcsString() {
         Setup();
         var r = Write(this, "name", "HELLO");
         Assert.Equal(true, r["ok"]); Assert.Equal("HELLO", r["newValue"]);
      }
      [Fact] public void SetsEnumByName() {
         Setup();
         var r = Write(this, "kind", "FLYING");
         Assert.Equal("FLYING", r["newValue"]);
      }
      [Fact] public void SetsInteger() {
         Setup();
         var r = Write(this, "power", 50);
         Assert.Equal(50, r["newValue"]);
      }
      [Fact] public void SetsFlag() {
         Setup();
         var r = Write(this, "flags", true, flag: "b");
         Assert.Equal(true, r["ok"]); Assert.Equal("b", r["flag"]); Assert.Equal(1, r["newValue"]);
      }
      [Fact] public void BadEnumReturnsError() {
         Setup();
         var r = Write(this, "kind", "NOPE");
         Assert.True(r.ContainsKey("error"));
         Assert.Contains("Options", (string)r["error"]);
      }
      [Fact] public void FlagOnNonBitArrayReturnsError() {
         Setup();
         var r = Write(this, "power", true, flag: "x");
         Assert.True(r.ContainsKey("error"));
      }
      [Fact] public void IntegerTypeMismatchReturnsError() {
         Setup();
         var r = Write(this, "power", "abc");
         Assert.True(r.ContainsKey("error"));
      }
   }
}
```

(The table-definition string mirrors `TupleTests.cs:51` (`^table[value:|t|a:|b:]1`) and the enum-source pattern `kind.types`. If the model doesn't parse it as intended, adjust the field widths to match those reference tests — then keep the assertions.)

- [ ] **Step 2: Run the tests to verify they fail.** Run: `(cd src/HexManiac.Tests && dotnet test --filter "FullyQualifiedName~RomAutomationWriteTests")` — Expected: compile error / FAIL, because `WriteValue` has no `(object, flag)` overload yet. (The test project targets the SDK-6 toolchain like the rest of the repo; if `dotnet test` from that dir needs a specific SDK, run it the same way the repo's other unit tests are run.)

- [ ] **Step 3: Replace `RomAutomation.WriteValue` with the typed engine.** In `src/HexManiac.Core/RomAutomation.cs`, replace the existing `WriteValue` method (the `int value` one) with:

```csharp
public static object WriteValue(IDataModel model, Func<ModelDelta> token,
      string table, int index, string field, object value, string flag = null) {
   var t = model.GetTableModel(table, token);
   if (t == null) return Err($"No table named '{table}'.");
   if (index < 0 || index >= t.Count) return Err($"index {index} out of range (0..{t.Count - 1}).");
   var element = t[index];
   if (!element.HasField(field)) return Err($"No field '{field}' on table '{table}'.");
   var seg = element.Table.ElementContent.First(s => s.Name == field);
   try {
      if (flag != null) {
         var flags = FlagNames(model, seg);
         if (flags == null) return Err($"Field '{field}' is not a bit-array; 'flag' only applies to bit-array fields.");
         if (!flags.Contains(flag)) return Err($"Unknown flag '{flag}' on field '{field}'. Flags: {string.Join(", ", flags)}");
         if (value is not bool && value is not int) return Err($"Flag '{flag}' expects true/false (or 0/1).");
         var oldFlag = ((ModelTupleElement)element[field])[flag];
         ((ModelTupleElement)element[field])[flag] = value;
         var newFlag = ((ModelTupleElement)t[index][field])[flag];
         return WriteResult(table, index, field, flag, oldFlag, newFlag);
      }
      if (seg is ArrayRunEnumSegment enumSeg) {
         if (value is string es) {
            if (!enumSeg.TryParse(model, es, out _))
               return Err($"Unknown value '{es}' for enum field '{field}'. Options: {string.Join(", ", enumSeg.GetOptions(model))}");
            var old = element[field];
            element[field] = es;
            return WriteResult(table, index, field, null, old, t[index][field]);
         }
         if (value is int ei) {
            var old = element[field];
            element[field] = ei;
            return WriteResult(table, index, field, null, old, t[index][field]);
         }
         return Err($"Field '{field}' is an enum; provide a name (string) or index (number).");
      }
      if (seg.Type == ElementContentType.PCS || seg.Type == ElementContentType.Pointer) {
         if (value is not string ps) return Err($"Field '{field}' is text; provide a string value.");
         var old = element.GetStringValue(field);
         element[field] = ps;
         return WriteResult(table, index, field, null, old, t[index].GetStringValue(field));
      }
      if (seg.Type == ElementContentType.Integer) {
         if (value is not int iv) return Err($"Field '{field}' is an integer; provide a number value.");
         var old = element.GetValue(field);
         element[field] = iv;
         return WriteResult(table, index, field, null, old, t[index].GetValue(field));
      }
      if (seg is ArrayRunBitArraySegment || seg is ArrayRunTupleSegment)
         return Err($"Field '{field}' is a bit-array; specify 'flag' to set a checkbox.");
      return Err($"Field '{field}' has a type that cannot be written.");
   } catch (Exception ex) {
      return Err($"Failed to set '{field}': {ex.Message}");
   }
}

private static IReadOnlyList<string> FlagNames(IDataModel model, ArrayRunElementSegment seg) =>
   seg is ArrayRunBitArraySegment b ? b.GetOptions(model)
   : seg is ArrayRunTupleSegment tp ? tp.Elements.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.Name).ToList()
   : null;

private static Dictionary<string, object?> WriteResult(string table, int index, string field, string flag, object oldV, object newV) {
   var d = new Dictionary<string, object?> {
      ["ok"] = true, ["table"] = table, ["index"] = index, ["field"] = field,
      ["oldValue"] = oldV, ["newValue"] = newV,
   };
   if (flag != null) d["flag"] = flag;
   return d;
}
```

- [ ] **Step 4: Fix the two existing call sites so all projects compile.** The old callers pass `int`; `int` boxes to `object` automatically, so only confirm they still compile (no signature mismatch). They are:
  - `src/HexManiac.Mcp/RomTools.cs` (~line 74): `() => RomAutomation.WriteValue(session.Require(), () => session.Token, table, index, field, value)` — `value` is still `int` here in Task 1; it boxes to `object`, `flag` defaults null. Leave as-is.
  - `src/HexManiac.WPF/AutomationPipeServer.cs` (~line 77): `RomAutomation.WriteValue(vp.Model, () => vp.CurrentChange, Str(p,"table"), Int(p,"index",-1), Str(p,"field"), Int(p,"value",0))` — `Int(...)` is `int`, boxes to `object`. Leave as-is.

- [ ] **Step 5: Build all three projects.**
  - `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` — Expected: 0 errors.
  - `dotnet build -c Release -p:PlatformTarget=x64` — Expected: Build succeeded (GUI + Core + Tests). Close any running GUI first if a file lock appears.

- [ ] **Step 6: Run the unit tests to verify they pass.** Run: `(cd src/HexManiac.Tests && dotnet test --filter "FullyQualifiedName~RomAutomationWriteTests")` — Expected: 7/7 pass.

- [ ] **Step 7: Run the headless gate (regression).** Ensure no GUI: `taskkill //F //IM HexManiacAdvance.exe` (ignore "not found"). Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN`. The integer `write_value` path is unchanged behavior (boxed int → Integer branch), and `newValue` for an integer is still a JSON number (`50`/`99`), so the existing `newValue`/`hp=99` assertions still pass.

- [ ] **Step 8: Commit.**
```bash
git add src/HexManiac.Core/RomAutomation.cs src/HexManiac.Tests/RomAutomationWriteTests.cs
git commit -m "Typed RomAutomation.WriteValue (string/enum/integer/flag) + Core tests"
```

---

## Task 2: MCP `write_value` accepts JSON value + `flag`

**Files:**
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `test/mcp-smoke.sh`

**Interfaces:**
- Consumes: `RomAutomation.WriteValue(model, token, table, index, field, object value, string flag = null)` (Task 1); existing `Dispatch(method, liveParams, tab, tabFile, headless)`.
- Produces: MCP tool `write_value(table, index, field, value, flag = null, tab = null, tabFile = null)` where `value` is a `System.Text.Json.JsonElement` (string/number/bool).

- [ ] **Step 1: Write the failing test — extend `test/mcp-smoke.sh`.** Add driver calls after the existing `write_value` line (id:5). Use the move-names table for a string and `data.pokemon.stats` (field `type1`) for an enum (FireRed has `type1`/`type2` enum fields on the stats table). Insert after the `id:5` line:

```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.moves.names","index":19,"field":"name","value":"SMOKETEST"}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.moves.names","start":19,"count":1}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"type1","value":"FLYING"}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"type1","value":"NOTATYPE"}}}'; sleep 2
```

Add assertions after the existing `write_value` assertion block:

```bash
# typed writes (string + enum-by-name) and a bad-enum error
[ "$(result_text 20 | jq -r '.newValue' 2>/dev/null)" = "SMOKETEST" ] && ok "write_value string newValue" || bad "write_value string"
[ "$(result_text 21 | jq -r '.rows[0].name' 2>/dev/null)" = "SMOKETEST" ] && ok "string write read-back" || bad "string write read-back"
[ "$(result_text 22 | jq -r '.newValue' 2>/dev/null)" = "FLYING" ] && ok "write_value enum-by-name" || bad "write_value enum"
[ "$(result_text 23 | jq -r '.error' 2>/dev/null | grep -ci 'Options')" -ge 1 ] && ok "bad enum lists options" || bad "bad enum error"
```

(Confirmed against the live ROM: `data.pokemon.stats` has fields `…, type1, type2, …` (they read back as raw enum indices, e.g. `type1: 12`). The enum write's `newValue` is the option *name* (`"FLYING"`) because the engine reports enums via the name getter. If for some reason `type1` does not parse as an enum on this ROM, pick another enum field via `read_table data.pokemon.stats start=1 count=1` and update the four lines.)

- [ ] **Step 2: Run the gate to verify it fails.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: FAIL — `write_value` currently rejects/ignores string values (its `value` param is `int`), so id:20/22 don't set strings and the new assertions fail.

- [ ] **Step 3: Widen the `write_value` tool.** In `src/HexManiac.Mcp/RomTools.cs`, replace the `WriteValue` method with (note `using System.Text.Json;` is already present):

```csharp
   [McpServerTool(Name = "write_value")]
   [Description("Set a field on a table row. value is a string (text/enum name), number (integer/enum index), or true/false. For a bit-array checkbox, pass flag=\"<name>\" with value true/false. Live GUI when present (visible+undoable); else headless.")]
   public string WriteValue(
      RomSession session,
      [Description("Anchor/table name")] string table,
      [Description("Row index")] int index,
      [Description("Field name within the row")] string field,
      [Description("New value: string, number, or true/false (type decided by the field)")] JsonElement value,
      [Description("Optional: name of one checkbox within a bit-array field")] string? flag = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var v = JsonToValue(value);
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["field"] = field, ["value"] = v };
      if (!string.IsNullOrEmpty(flag)) p["flag"] = flag;
      return Dispatch("write_value", p, tab, tabFile,
         () => RomAutomation.WriteValue(session.Require(), () => session.Token, table, index, field, v, flag));
   }

   // Convert a JSON scalar argument to the CLR value the engine expects.
   private static object? JsonToValue(JsonElement v) => v.ValueKind switch {
      JsonValueKind.String => v.GetString(),
      JsonValueKind.Number => v.TryGetInt32(out var i) ? i : v.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => v.ToString(),
   };
```

- [ ] **Step 4: Build the MCP server.** Run: `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` — Expected: 0 errors. (If the SDK rejects a `JsonElement` tool parameter, declare it `object value` instead and keep `JsonToValue((JsonElement)value)`; ModelContextProtocol binds untyped scalars to `JsonElement` at runtime.)

- [ ] **Step 5: Run the gate to verify it passes.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: `ALL GREEN`, including the four new assertions. (The original integer `write_value`/`hp=99` assertions still pass.)

- [ ] **Step 6: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs test/mcp-smoke.sh
git commit -m "MCP write_value accepts JSON value (string/number/bool) + flag"
```

---

## Task 3: Live pipe handler typed write + flag

**Files:**
- Modify: `src/HexManiac.WPF/AutomationPipeServer.cs`
- Modify: `test/mcp-live-smoke.sh`

**Interfaces:**
- Consumes: `RomAutomation.WriteValue(..., object value, string flag = null)`; existing helpers `ResolveTab`, `NoTab`, `Ok`, `Str`, `Int`, `StrOrNull`.
- Produces: pipe `write_value` accepting `value` as string/number/bool and optional `flag`; private `static object Val(JsonElement p, string key)`.

- [ ] **Step 1: Write the failing test — extend `test/mcp-live-smoke.sh`.** Add driver calls after the existing id:8 `goto` line (inside the `{ ... } | "./$MCP"` block):

```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.moves.names","index":19,"field":"name","value":"LIVESTR"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":10,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.moves.names","start":19,"count":1}}}'; sleep 1
```

Add assertions after the existing live assertions:

```bash
[ "$(rt 9 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "typed write mode=live" || bad "typed write not live"
[ "$(rt 9 | jq -r '.newValue' 2>/dev/null)" = "LIVESTR" ] && ok "live string write newValue" || bad "live string write"
[ "$(rt 10 | jq -r '.rows[0].name' 2>/dev/null)" = "LIVESTR" ] && ok "live string read-back" || bad "live string read-back"
```

- [ ] **Step 2: Run the live gate to verify it fails.** Run: `bash test/mcp-live-smoke.sh` — Expected: FAIL — the pipe `write_value` still reads `value` as int (`Int(p,"value",0)`), so a string value is lost and `newValue`/read-back are not `LIVESTR`.

- [ ] **Step 3: Update the pipe `write_value` handler + add `Val`.** In `src/HexManiac.WPF/AutomationPipeServer.cs`, replace the `case "write_value":` body with:

```csharp
            case "write_value": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               // Edit through the tab's change token so it enters GUI undo history
               // and renders immediately.
               return Ok(RomAutomation.WriteValue(vp.Model, () => vp.CurrentChange,
                  Str(p, "table"), Int(p, "index", -1), Str(p, "field"), Val(p, "value"), StrOrNull(p, "flag")));
            }
```

And add this helper next to the other `Str`/`Int` helpers:

```csharp
      // Read a JSON scalar param as the CLR value the engine expects.
      private static object Val(JsonElement p, string key) {
         if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(key, out var v)) return 0;
         return v.ValueKind switch {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (object)v.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => v.ToString(),
         };
      }
```

- [ ] **Step 4: Build the GUI.** Run: `dotnet build -c Release -p:PlatformTarget=x64` — Expected: Build succeeded. (Close any running GUI first if a file lock appears.)

- [ ] **Step 5: Run the live gate to verify it passes.** Run: `bash test/mcp-live-smoke.sh` — Expected: `LIVE GREEN`, including the three new assertions.

- [ ] **Step 6: Run the headless gate (regression).** Ensure no GUI running. Run: `bash test/mcp-smoke.sh` — Expected: `ALL GREEN`.

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-live-smoke.sh
git commit -m "Live pipe: typed write_value (string/number/bool) + flag"
```

---

## Task 4: Docs

**Files:**
- Modify: `BUILD.md`

- [ ] **Step 1: Document the extended `write_value`.** In `BUILD.md`, near the tool descriptions / live-mode section, add:

```markdown
### Typed write_value

`write_value` sets any field, not just integers. `value` is interpreted by the
field's type:

- text (name) and pointer-to-text (description, effect) ← a **string**
- integer (power, accuracy, pp) ← a **number**
- enum (type) ← the option **name** (e.g. `"FLYING"`) or a number index
- bit-array checkbox (e.g. a move's `target`) ← pass `flag="<name>"` with
  `value` `true`/`false`

Bad values return a clear error (e.g. an unknown enum value lists the valid
options). Works live (visible + undoable in the GUI) and headless.
```

- [ ] **Step 2: Commit.**
```bash
git add BUILD.md
git commit -m "Docs: typed write_value in BUILD.md"
```

---

## Self-Review notes

- **Spec coverage:** tool surface incl. `flag` (T2); engine via unified setter + nested flag (T1); validation/error messages — bad enum lists options, flag-on-non-bitarray, unknown flag, type mismatch (T1, asserted in T1 unit tests + T2 gate); live/headless wiring + `mode` (T2 MCP, T3 pipe); backward-compatible integer path (T1 Step 7, T2 Step 5 regression); Core unit tests (T1), headless gate (T2), live gate (T3), docs (T4). All spec sections covered.
- **Headless invariant:** `test/mcp-smoke.sh` run in T1 (Step 7), T2 (Step 5), T3 (Step 6).
- **Type consistency:** `RomAutomation.WriteValue(model, token, table, index, field, object value, string flag = null)` defined T1, called identically in T2 (MCP headless) and T3 (pipe live). Result keys (`ok, table, index, field, flag, oldValue, newValue` / `error`) defined in T1 `WriteResult`/`Err`, asserted in T1 tests and T2/T3 gates. `JsonToValue` (T2) and `Val` (T3) coerce the same JSON kinds to the same CLR types the engine branches on (`string`/`int`/`bool`).
- **Note on enum field name:** T2 Step 1 assumes the FireRed stats enum field is `type1`; the step says to verify via `read_table` and adjust if different — flagged, not silently assumed.
