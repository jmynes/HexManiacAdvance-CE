# MCP Documentation + Runtime Help — Design Spec

**Date:** 2026-06-20
**Status:** Approved (user approved the design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (branch `mcp-integration`)

## Goal

Give the HexManiacAdvance MCP first-class documentation: a hand-written user guide
in the repo **and** a way to read that guide *while using the MCP* — a `help`
tool and MCP resources — so a new or non-developer user can discover what the
tools do and how to use them without reading source.

## Single source, three surfaces

1. **`docs/MCP.md`** (hand-written) — the canonical guide.
2. The same file is **embedded into the MCP assembly** (csproj `EmbeddedResource`,
   `LogicalName` e.g. `HexManiac.Mcp.MCP.md`) so the running server serves identical
   text regardless of working directory (the exe runs from `artifacts/…`, far from
   the repo `docs/`).
3. Both the `help` tool and the MCP resource read that embedded copy.

## `docs/MCP.md` contents

Aimed at users (not contributors): what the MCP is; quick start + `.mcp.json`
setup; the **live-vs-headless** model and `mode` field; the **open-a-ROM-first**
workflow; `write_value` value types (string / number / enum-name / bit-array
`flag`, friendly checkbox names); the **metadata warning** for unrecognized ROMs;
**save/backup safety** (`save_rom` needs `outPath` or `overwrite=true`); a grouped
**tool reference** (read, typed write, navigation, editor ops, ROM/tab lifecycle)
each with a one-line summary + a copy-paste example; common **recipes**; and
**troubleshooting** (e.g. reconnect after a rebuild, "needs metadata choice").
Linked from `README.md`.

## `help(topic = null)` tool

- **No topic** → the full guide text (the embedded `MCP.md`). It is user-initiated,
  so returning the whole guide is acceptable and simplest/robust.
- **`topic`** → the guide **section** whose heading matches `topic`
  (case-insensitive substring on `##`/`###` headings); if no heading matches,
  return a short "no section named '<topic>'; sections: <list>" with the heading
  list. `topic` may name a tool (tool names appear as headings/anchors in the
  reference) or a section.
- Result is the markdown text (a plain string). Live/headless irrelevant — it
  reads the embedded doc, so it works in both; still stamped with `mode` via the
  existing wrapper for consistency. Tool count 20 → 21.

## MCP resources

Expose the guide so capable clients can list/preview it:
- `hexmaniac://guide` → the full embedded `MCP.md` (mime `text/markdown`).
- `hexmaniac://tools` → a generated index of the tools (name + one-line
  description), or the guide's tool-reference section if generation is awkward.

Implemented via the ModelContextProtocol resource API
(`[McpServerResourceType]`/`[McpServerResource]` + the assembly's resource
registration). **Exact 1.4.0 resource API to be confirmed during implementation;**
if resources are not cleanly supported, the `help` tool remains the universal
path and resources become a documented follow-up (the plan must verify this in
its first task and report, not silently drop it).

## Components & boundaries

- **`docs/MCP.md`** — content (also the embedded resource).
- **`src/HexManiac.Mcp/HexManiac.Mcp.csproj`** — `EmbeddedResource` for `MCP.md`.
- **`src/HexManiac.Mcp/`** — a small `Docs` helper (load the embedded markdown,
  split into sections by heading); the `help` tool (in `RomTools` or a new
  `HelpTool`); the resource type.
- No Core / WPF changes (MCP project only).

## Error handling

`help` with an unknown topic → a helpful "sections: …" message, never an error/crash.
Missing embedded resource (build misconfig) → a clear error string.

## Testing

- **Headless gate** (`test/mcp-smoke.sh`, ALL GREEN; tool count → 21):
  - `tools/call help` (no args) → text contains known anchors (`open_rom`,
    `write_value`, `save_rom`).
  - `tools/call help {topic:"write_value"}` → text mentions value types / `flag`.
  - `tools/call help {topic:"nonsense"}` → the "sections:" fallback (not an error).
  - `resources/list` includes `hexmaniac://guide`; `resources/read` of it returns
    non-empty markdown. (If the SDK resource surface differs, adapt the assertions
    to the actual `resources/*` JSON-RPC shape; if resources are unsupported in
    1.4.0, drop these two and document it — flag in the report.)

## Out of scope

Auto-generating per-tool param schemas into the guide (the tool `[Description]`s
already cover params in `tools/list`); localization; versioned docs.
