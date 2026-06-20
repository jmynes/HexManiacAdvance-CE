# Checkbox Flag-Name Normalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or executing-plans). Checkbox (`- [ ]`) steps.

**Goal:** Make every bit-array checkbox settable via `write_value`'s `flag` by friendly name, including flags whose names contain spaces (currently rejected because HexManiac returns them quote-wrapped).

**Architecture:** One change in `RomAutomation.WriteValue` (Core): resolve the caller's `flag` against the option names ignoring surrounding quotes and case, apply with the matched original option. Shared by both backends.

**Tech Stack:** C# / .NET (Core net6.0, MCP net8.0), bash + jq gate.

## Global Constraints

- Headless `test/mcp-smoke.sh` stays ALL GREEN; uses the recognized firered (no GUI).
- No new tool; no API change; Core-only.
- Single-word flags keep working (normalization is a no-op on them).
- Build: MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; GUI `dotnet build -c Release -p:PlatformTarget=x64`. Headless gate with NO GUI (`taskkill //F //IM HexManiacAdvance.exe`; `//IM HexManiac.Mcp.exe`).

---

## Task 1: Normalize flag-name matching

**Files:**
- Modify: `src/HexManiac.Core/RomAutomation.cs`
- Modify: `test/mcp-smoke.sh`

**Interfaces:**
- Consumes: existing `RomAutomation.WriteValue` flag block, `FlagNames`, `TryCoerceFlag`, `ModelTupleElement` indexer. `System.Linq` already imported.
- Produces: friendly-name flag resolution (quote/case-insensitive).

- [ ] **Step 1: Write the failing test — extend `test/mcp-smoke.sh`.** After the existing driver lines (in the `{ ... } | "./$EXE"` block), add (set a spaced-name move checkbox by friendly name, and a single-word one as regression). Move index 1 is POUND; `info` is a bit-array, `target` is a bit-array:
```bash
  printf '%s\n' '{"jsonrpc":"2.0","id":39,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.moves.stats.battle","index":1,"field":"info","value":false,"flag":"Makes Contact"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":40,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.moves.stats.battle","index":1,"field":"target","value":true,"flag":"Both"}}}'; sleep 1
```
Add assertions:
```bash
[ "$(result_text 39 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "checkbox by spaced friendly name" || bad "spaced flag name"
[ "$(result_text 39 | jq -r '.flag' 2>/dev/null)" = "Makes Contact" ] && ok "flag reported unquoted" || bad "flag unquoted report"
[ "$(result_text 40 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "single-word flag still works" || bad "single-word flag"
```

- [ ] **Step 2: Run the gate to verify it fails.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: FAIL — id:39 returns `{error:"Unknown flag 'Makes Contact' ..."}` (current exact match rejects the unquoted spaced name), so `.ok` is null and `.flag` mismatches.

- [ ] **Step 3: Apply the fix.** In `src/HexManiac.Core/RomAutomation.cs`, replace the flag block inside `WriteValue` (the `if (flag != null) { ... }` body) with:

```csharp
            if (flag != null) {
               var flags = FlagNames(model, seg);
               if (flags == null) return Err($"Field '{field}' is not a bit-array; 'flag' only applies to bit-array fields.");
               static string NormFlag(string s) => s.Trim().Trim('"');
               var match = flags.FirstOrDefault(f => NormFlag(f).Equals(NormFlag(flag), StringComparison.OrdinalIgnoreCase));
               if (match == null) return Err($"Unknown flag '{flag}' on field '{field}'. Flags: {string.Join(", ", flags.Select(NormFlag))}");
               if (!TryCoerceFlag(value, out var flagVal)) return Err($"Flag '{flag}' expects true/false (or 0/1).");
               var oldFlag = ((ModelTupleElement)element[field])[match];
               ((ModelTupleElement)element[field])[match] = flagVal;
               var newFlag = ((ModelTupleElement)t[index][field])[match];
               return WriteResult(table, index, field, NormFlag(match), oldFlag, newFlag);
            }
```
(Only the flag block changes: exact `Contains(flag)` → quote/case-insensitive match via `NormFlag`; apply and report using the matched option. `StringComparison` is in `System`, already imported.)

- [ ] **Step 4: Build MCP + GUI.** `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` and `dotnet build -c Release -p:PlatformTarget=x64`. Expected: 0 errors.

- [ ] **Step 5: Run the gate to verify it passes.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: `ALL GREEN`, incl. `checkbox by spaced friendly name`, `flag reported unquoted`, `single-word flag still works`; all prior assertions still pass (the existing headless `flag` behavior is unchanged for single-word names).

- [ ] **Step 6: Commit.**
```bash
git add src/HexManiac.Core/RomAutomation.cs test/mcp-smoke.sh
git commit -m "write_value: resolve bit-array flag names ignoring quotes/case (spaced checkbox names)"
```

---

## Task 2: Docs

**Files:**
- Modify: `BUILD.md`

- [ ] **Step 1:** Under the typed `write_value` docs, add a note:
```markdown
- Bit-array checkbox flags are matched by friendly name, case-insensitive and
  ignoring the quotes HexManiac uses for names with spaces — e.g.
  `write_value(..., field="info", flag="Makes Contact", value=true)`.
```

- [ ] **Step 2:** `git add BUILD.md && git commit -m "Docs: friendly checkbox flag names"`.

---

## Self-Review notes

- **Spec coverage:** quote/case-insensitive flag match (T1); single-word regression + unquoted report asserted (T1); docs (T2). Pointer-to-script and derived fields are documented out-of-scope in the spec (no task). Covered.
- **Headless invariant:** gate run in T1 Step 5; recognized firered.
- **Type consistency:** only the flag block of `WriteValue` changes; `FlagNames`/`TryCoerceFlag`/`WriteResult`/`ModelTupleElement` indexer unchanged. `NormFlag` is a local static.
- **Test determinism:** firered move 1 (`POUND`) has bit-array `info` with spaced flag `Makes Contact` and `target` with single-word `Both` — both confirmed by the audit; the gate uses the recognized firered so no metadata guessing.
