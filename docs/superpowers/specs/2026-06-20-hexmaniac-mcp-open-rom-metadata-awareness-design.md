# MCP `open_rom` Metadata Awareness — Design Spec

**Date:** 2026-06-20
**Status:** Approved (self-approved per user delegation — user is offline, asked for autonomous overnight progress with no questions).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (HexManiacAdvance @ 0.5.6.1, branch `mcp-integration`)

## Goal

Make `open_rom` aware that HexManiac only has correct table offsets for a fixed
list of recognized base games (FireRed, Emerald, etc.); for any other ROM it
**guesses** offsets, so reads/writes may be wrong. When the MCP is asked to open a
ROM that is **not recognized and has no sidecar `.toml`**, warn the caller (in the
tool result — never via a human prompt) and offer choices, instead of silently
operating on guessed metadata.

## Background (verified in source)

- `IReadOnlyList<byte>.GetGameCode()` (`IDataModel.cs:376`) reads the 4-char GBA
  game code from the ROM header.
- `HardcodeTablesModel` (`:153`) and `PokemonModel` (`:264`) decode tables only
  when `singletons.GameReferenceTables.TryGetValue(gameCode, out tables)` succeeds.
  So **recognized ⇔ `Singletons.GameReferenceTables` contains the game code**;
  otherwise HMA guesses.
- HMA's sidecar metadata is `<rom-without-ext>.toml` next to the ROM (e.g.
  `firered.gba` ↔ `firered.toml`). The live GUI open loads it via
  `fileSystem.MetadataFor(...)`; **headless `RomSession.Load` currently ignores it**
  (`new StoredMetadata(new string[0])`).
- HMA writes a `.toml` on open when none exists (so opening then mutates the
  sidecar). This spec does not change that GUI behavior; it changes how the MCP
  *decides whether to proceed*.

## Tool surface — `open_rom(path, metadata = "auto")`

`metadata` is a string mode (the caller's "choice", returned by the warning):

- **`"auto"` (default):**
  - If the game is **recognized** OR a sidecar `<rom>.toml` **exists** → open
    normally (today's behavior). Result adds `gameCode`, `recognized` (bool),
    `metadataSource` (`"toml"` | `"builtin"`).
  - Else (unrecognized **and** no toml) → **do not open**; return a warning result
    (see below) telling the caller metadata would be guessed and how to proceed.
- **`"find_toml"`:** use the sidecar `<rom>.toml` if it exists (open with it,
  `metadataSource:"toml"`); else return `{ error: "No sidecar .toml found next to
  <rom> (looked for <path>.toml). Use metadata='guess' to open with guessed
  offsets, or metadata='guess_offsets'." }`.
- **`"guess_offsets"`:** return `{ error: "Auto-detecting table offsets for an
  unrecognized ROM is not yet implemented. Please bug jmynes on GitHub or
  jordank.memes on Discord for this feature." }` (no open).
- **`"guess"`:** open anyway with HexManiac's guessed metadata (today's
  empty-metadata load); result `metadataSource:"guessed"` plus a `warning` that
  offsets are guessed and may be inaccurate.

### Warning result (auto, unrecognized, no toml)
```json
{ "ok": false, "needsMetadataChoice": true, "gameCode": "<code>",
  "recognized": false, "hasToml": false,
  "warning": "<rom> is not a recognized base game (e.g. FireRed/Emerald) and has no sidecar .toml, so HexManiac would guess table offsets — reads and writes may be inaccurate.",
  "options": {
     "find_toml": "Re-call open_rom with metadata='find_toml' to use a .toml placed next to the ROM.",
     "guess_offsets": "Re-call with metadata='guess_offsets' to auto-detect offsets (not yet implemented).",
     "guess": "Re-call with metadata='guess' to open anyway with guessed metadata."
  },
  "mode": "live|headless" }
```

## Detection (shared, MCP layer)

Decided once in `RomTools.OpenRom`, before dispatching the actual open:
- `gameCode = File.ReadAllBytes(path)` → `((IReadOnlyList<byte>)bytes).GetGameCode()`.
- `recognized = session.Singletons.GameReferenceTables.TryGetValue(gameCode, out _)`.
- `tomlPath = Path.ChangeExtension(path, ".toml")`; `hasToml = File.Exists(tomlPath)`.

This needs only the path + `RomSession.Singletons` (already available); it does not
require loading the model first, so the warning short-circuits before any load.

## Live / headless wiring

- **Recognized / toml / `guess`** → open as today: `Dispatch("open_rom", {path}, …)`
  → live opens a new tab (`OpenFileAsTab`, which itself loads any sidecar toml);
  headless `session.Load(path)`.
- **`find_toml`** → headless must load the toml: add `RomSession.Load(path,
  string[] metadataLines)` (reads `tomlPath` and builds `new
  StoredMetadata(lines)`); live `OpenFileAsTab` already reads the sidecar toml, so
  the live path needs no change for find_toml.
- The detection/branching lives in `RomTools.OpenRom`; the warning and
  `guess_offsets`/`find_toml`-miss results are returned without dispatching an open.
- Every result carries `mode` (stamped via the existing `Stamp`/`Dispatch`).

## Backward compatibility

Recognized games (FireRed in the gates) keep opening with no friction under
`"auto"` — the headless and live smoke gates stay green (they open the recognized
firered, so `recognized=true` → normal open; existing assertions unaffected, aside
from the result gaining `gameCode`/`recognized`/`metadataSource` fields which the
gates don't assert against).

## Error handling

`{ error }` (or the structured warning), never a crash: file missing; unreadable
header; `guess_offsets` not-implemented; `find_toml` with no toml.

## Components & boundaries

- **`RomTools.OpenRom`** (MCP) — detection + `metadata`-mode branching + routing;
  the single place that decides recognized/toml and what to return.
- **`RomSession`** (MCP) — `Load(path)` unchanged; add `Load(path, string[]
  metadataLines)` for the `find_toml` headless path; expose `Singletons` (already
  public).
- No Core changes (uses existing `GetGameCode` / `GameReferenceTables` /
  `StoredMetadata`).

## Testing

- **Headless gate** (`test/mcp-smoke.sh`, stays ALL GREEN): the recognized firered
  still opens under `auto` (existing assertions pass); add an assertion that the
  `open_rom` result now reports `recognized:true` and `gameCode` for firered. Add a
  `guess_offsets` call on firered's path asserting the not-implemented message
  (the mode check doesn't require an unrecognized ROM — `guess_offsets` always
  returns the not-implemented notice).
- **Unrecognized-ROM unit-ish check (headless):** craft a tiny non-recognized
  "ROM" (a small byte file with an unknown game code) at a temp path with no toml,
  call `open_rom` under `auto`, assert `needsMetadataChoice:true` + the warning +
  `recognized:false`; then `metadata='guess'` opens it (ok). (If a byte file is
  rejected before the game-code read, use a copy of firered with its game-code
  bytes overwritten — the gate can do this with `dd`/`printf`. Implementation plan
  pins the exact fixture.)
- **Live gate:** unchanged (firered is recognized; opens normally). Optionally add
  a `guess_offsets` assertion mirroring headless.

## Out of scope (v1)

Actually auto-detecting offsets ("thumb around"); searching directories other than
the ROM's own folder for a toml; changing HMA's on-open toml-creation behavior.
