# MCP Checkbox Editability — Flag-Name Normalization — Design Spec

**Date:** 2026-06-20
**Status:** Approved (self-approved per user delegation — autonomous overnight work).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (branch `mcp-integration`)

## Goal

Make **every bit-array checkbox** of the pokemon / moves / items / trainers tables
editable via `write_value`'s `flag` by its **friendly name**, fixing the case where
a flag name contains spaces (HexManiac returns those option names quote-wrapped, so
the current exact-match rejects the friendly name).

This is the core of the user's request "every field and checkbox is editable" for
those tables. Scalar int/string/enum fields already work (audit confirmed); this
spec closes the checkbox gap.

## Background (from the audit: `.superpowers/sdd/feature-b-audit.md`)

- Scalar fields (int/string/enum) on all four tables are already settable via
  `write_value` (numbers, strings, enum-by-name).
- Bit-array checkbox fields with single-word flag names work (e.g. move `target`).
- **Gap:** move `info` (and any bit-array whose flag names contain spaces) — its
  flags are `Makes Contact`, `Affected by Protect`, `Affected by Magic Coat`,
  `Affected by Snatch`, `Affected by Mirror Move`, `Affected by King's Rock`.
  `ArrayRunBitArraySegment.GetOptions` returns these **wrapped in double quotes**
  (`"Makes Contact"`), so `write_value(..., flag:"Makes Contact")` (no quotes)
  fails `flags.Contains(flag)` → "Unknown flag". Passing the literal quoted string
  works, but that's not discoverable/ergonomic.

## Root cause

In `RomAutomation.WriteValue` (Core), the flag path does:
```csharp
var flags = FlagNames(model, seg);              // option names, possibly "quoted"
if (!flags.Contains(flag)) return Err(...);     // exact match — fails on quoted/space names
... ((ModelTupleElement)element[field])[flag] = flagVal;
```
The membership test is exact and case-sensitive, and `flags` may carry surrounding
quotes that the caller's friendly name lacks.

## Fix

Match the caller's `flag` against the option names **ignoring surrounding quotes and
case**, then apply using the matched original option (the `ModelTupleElement`
indexer matches the quoted form, which is verified to work):

```csharp
var flags = FlagNames(model, seg);
if (flags == null) return Err($"Field '{field}' is not a bit-array; 'flag' only applies to bit-array fields.");
static string NormFlag(string s) => s.Trim().Trim('"');
var match = flags.FirstOrDefault(f => NormFlag(f).Equals(NormFlag(flag), System.StringComparison.OrdinalIgnoreCase));
if (match == null) return Err($"Unknown flag '{flag}' on field '{field}'. Flags: {string.Join(", ", flags.Select(NormFlag))}");
if (!TryCoerceFlag(value, out var flagVal)) return Err($"Flag '{flag}' expects true/false (or 0/1).");
var oldFlag = ((ModelTupleElement)element[field])[match];
((ModelTupleElement)element[field])[match] = flagVal;
var newFlag = ((ModelTupleElement)t[index][field])[match];
return WriteResult(table, index, field, NormFlag(match), oldFlag, newFlag);
```

Behavioral effect:
- `flag:"Makes Contact"` (no quotes), `flag:"makes contact"` (any case), and
  `flag:"\"Makes Contact\""` (quoted) all resolve to the same checkbox.
- Single-word flags (`Both`, `Self`, …) keep working (NormFlag is a no-op on them).
- The error message now lists **unquoted** flag names (cleaner/discoverable).
- The reported `flag` in the result is the normalized (unquoted) name.

## Out of scope (documented; not "drawer checkbox" scalars)

- **Pointer-to-script fields** (item `fieldeffect`, `battleeffect`) — pointers to
  code/effect runs, not simple values/checkboxes; like sprites/palettes, they need
  a dedicated approach (repoint-by-address or a sub-editor). Noted as a follow-up.
- **Derived fields** (`baseStatTotal` = sum of stats; trainer `prizeMoney` =
  from class) — computed, not independently stored; setting them is meaningless
  (they silently recompute). These should not be "editable"; noted as expected.

## Components & boundaries

- **`RomAutomation.WriteValue`** (Core) — the only change: the flag-resolution block.
  `NormFlag` is a local static. No new public API. Both backends benefit (shared).

## Testing

- **Headless gate** (`test/mcp-smoke.sh`, recognized firered, no GUI): set a
  spaced-name checkbox by friendly name — `write_value(data.pokemon.moves.stats.battle,
  index 1, field "info", flag "Makes Contact", value false)` → `ok:true` with the
  flag reported as the unquoted name; before the fix this returns "Unknown flag".
  Also assert the single-word `target` flag (e.g. `Both`) still works (regression).
- The existing live-gate `info`/`target` behavior is unaffected.

## Build impact

Core-only change; build commands unchanged. Headless gate stays ALL GREEN.
