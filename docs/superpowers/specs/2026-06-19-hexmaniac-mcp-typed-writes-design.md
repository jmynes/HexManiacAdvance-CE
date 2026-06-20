# Typed `write_value` (strings, enums, bit-array flags) — Design Spec

**Date:** 2026-06-19
**Status:** Approved (design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (HexManiacAdvance @ 0.5.6.1, branch `mcp-integration`)
**Builds on:** `2026-06-17-hexmaniac-mcp-live-gui-design.md`, `2026-06-17-hexmaniac-mcp-goto-design.md`

## Goal

Let the MCP `write_value` tool edit **any** table field, not just integers — Pokémon/move
names, descriptions, effects, the `type` enum (by name), and the individual named
checkboxes of a bit-array field such as a move's `target`. Today `write_value` is
integer-only, so strings and enum-by-name require hand-written HMA scripts.

## Background

`HexManiac.Core` already has a unified, type-aware setter on `ModelArrayElement`
(`src/HexManiac.Core/Models/ModelTable.cs`): `element[fieldName] = value` dispatches on
the field's segment type:

- **PCS** (inline string) → `SetStringValue`
- **Pointer**-to-text → `SetStringValue` (relocates/expands the text run) — used by
  `description`/`effect`
- **Enum** (`ArrayRunEnumSegment`) → `SetEnumValue` for a string option name (e.g.
  `"FLYING"`), or `SetValue` for an int index
- **Integer** / **BitArray** → `SetValue` (int)

A **BitArray** field's individual flags are reached one level down: `element[field]`
returns a `ModelTupleElement`, and `tuple[flagName] = bool|int` sets one checkbox.
`ModelArrayElement` also exposes `HasField(name)` and `Serialize(field)` (renders any
field's display value — string / enum name / int — as a string).

The current `RomAutomation.WriteValue` takes `int value` and calls the integer-only
`SetValue`. This feature routes it through the unified setter instead.

## Non-goals

- No change to read tools, `goto`, `list_shortcuts`, `save_rom`, `run_script`.
- No new MCP tool — `write_value` is extended in place.
- Setting an entire bit-array as one integer mask is **not** a supported convenience
  (named flags via `flag` are the supported path). Integers still write to plain
  Integer fields exactly as before.

## Tool surface — `write_value` (extended)

```
write_value(table, index, field, value, flag = null, tab = null, tabFile = null)
```

- **`value`** changes from int-only to a **JSON value**: string, number, or bool.
  The target field's segment type — not the caller — decides interpretation.
- **`flag`** (new, optional): the name of one checkbox within a bit-array `field`.
  When present, `value` is a bool (or 0/1) applied to that single flag.
- `tab` / `tabFile` unchanged (live tab selector).

Value-to-field mapping:

| `field` example | Segment type | `value` (and `flag`) |
|---|---|---|
| `name` | PCS | string |
| `description`, `effect` | Pointer-to-text | string |
| `power`, `accuracy`, `pp` | Integer | number |
| `type` | Enum | string option name (`"FLYING"`) or number index |
| `target` (one checkbox) | BitArray | `flag="<name>"`, `value=true`/`false` (or `1`/`0`) |

**Backward compatibility:** a numeric `value` with no `flag`, targeting an Integer
field, behaves exactly as today. The headless smoke gate's existing `write_value`
assertion stays green.

## Engine — `RomAutomation.WriteValue` (shared, in Core)

New signature (single implementation used by both backends):

```
object WriteValue(IDataModel model, Func<ModelDelta> token,
                  string table, int index, string field, object value, string flag = null)
```

Logic:

1. Resolve `t = model.GetTableModel(table, token)`; validate non-null, `index` in range,
   `element.HasField(field)`.
2. **No `flag`:** validate/convert `value` against the field's segment type (see
   Validation), then `element[field] = value`.
3. **With `flag`:** the `field` must be a bit-array; obtain its `ModelTupleElement`
   (`element[field]`), validate `tuple.HasField(flag)`, coerce `value` to bool/int, then
   `tuple[flag] = value`.
4. Capture old/new for the response with `element.Serialize(field)` (whole field) or the
   flag's before/after value (flag path).

Return shape (mirrors today's, with display-friendly values):

```json
{ "ok": true, "table": "...", "index": N, "field": "...",
  "flag": "<name or omitted>", "oldValue": "<display>", "newValue": "<display>" }
```

## Validation & error handling

Every failure returns `RomAutomation.Err(message)` (`{ "error": ... }`) — never a crash,
never a silent no-op:

- Unknown `table`, `index` out of range, unknown `field` — as today.
- **Enum, bad string value:** the existing `SetEnumValue` silently no-ops when the option
  is unrecognized. The engine MUST detect this (validate against the segment's options
  before/after, or check the parse result) and return
  `"Unknown value '<v>' for enum field '<field>'. Options: <comma-list>"`.
- **`flag` on a non-bit-array field:** `"Field '<field>' is not a bit-array; 'flag' only
  applies to bit-array fields."`
- **Unknown `flag` name:** `"Unknown flag '<flag>' on field '<field>'. Flags: <comma-list>"`
  (do not let the underlying `.First()` throw).
- **Type mismatch:** a string into an Integer/enum-index field, or a non-bool into a flag,
  etc., is caught and returned as `"Field '<field>' expects <type>; got <kind>."` rather
  than an uncaught cast exception.

## Live / headless wiring

- **MCP layer (`RomTools.WriteValue`):** `value` becomes a `JsonElement`; convert to
  `string` / `int` / `bool` before use. Add the optional `flag` string param. Headless
  path calls `RomAutomation.WriteValue` on `session.Require()` with `() => session.Token`.
  Live path forwards `value` (+ `flag`) in the pipe params via the existing `Dispatch`.
- **Pipe server (`AutomationPipeServer`, `write_value` case):** read `value` as an object
  (string/number/bool) instead of int-only, read optional `flag`, and call the same
  `RomAutomation.WriteValue` on the resolved GUI tab's model through `() => vp.CurrentChange`
  (so edits remain visible and undoable in the GUI).
- Result still carries `mode: "live" | "headless"`; live errors surface as `mode:"live"`
  (per the prior Dispatch fix).

## Components & boundaries

- **`RomAutomation.WriteValue`** (Core) — the one place the typed-write logic and validation
  live; both backends call it. Single responsibility: validate + apply one field/flag write
  and report old/new.
- **`RomTools.WriteValue`** (MCP) — JSON→CLR value coercion, `flag` param, route live/headless.
- **`AutomationPipeServer`** (`write_value` case) — JSON→CLR coercion on the GUI side, calls
  the shared engine on the UI thread.

## Testing

- **Core unit tests** (`HexManiac.Tests`, no GUI — where the type logic actually lives):
  - set a PCS string and read it back; set an enum by name (`type`) and read back;
  - set an Integer field (regression); set a bit-array flag `true`/`false` and read back;
  - bad enum value returns an error listing options; `flag` on a non-bit-array field errors;
    unknown flag errors; type-mismatch errors.
- **Headless gate (`test/mcp-smoke.sh`, stays ALL GREEN):** keep the existing integer write;
  add a string-name write + read-back, an enum-by-name write + read-back, and one bad-value
  error assertion (all `mode:"headless"`).
- **Live gate (`test/mcp-live-smoke.sh`, LIVE GREEN):** add a string write and a `flag`
  toggle against the running GUI, asserting read-back and `mode:"live"`.

## Build impact

No new projects/SDK changes; build commands unchanged. `RomAutomation` stays in Core
(net6.0), referenced by both WPF (net6.0) and MCP (net8.0).
