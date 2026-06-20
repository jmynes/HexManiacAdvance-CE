# MCP ROM Reference + Identify — Design Spec

**Date:** 2026-06-20
**Status:** Approved (user approved the design + both add-ons + base-detection).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (branch `mcp-integration`)

## Goal

Make the supported-ROM reference (game codes, No-Intro names, checksums, HMA support
tiers) **part of the repo** and **available to the MCP at runtime without the repo**,
and let the MCP **identify** a loaded ROM: is it a clean dump, which game/revision,
and — for a romhack — what base game it's built on.

## 1. Data source of truth — `src/HexManiac.Mcp/resources/supported-roms.json`

Git-tracked (so it's in the repo) **and** embedded into the MCP assembly
(csproj `EmbeddedResource`, `LogicalName = HexManiac.Mcp.supported-roms.json`) so the
server serves it regardless of working directory / repo presence. Shape:

```json
{
  "description": "Pokemon GBA base games and HexManiacAdvance support levels.",
  "recognition": "HMA identifies games by the 4-char header code + version byte (e.g. BPRE0), not by hash; a romhack of a supported base keeps that code and stays supported.",
  "supportTiers": {
    "prime": "Primary, best-supported targets (FireRed v1.0, Emerald).",
    "full": "Complete reference tables in HMA (tableReference.txt / HardcodeTablesModel).",
    "reduced": "Default metadata only — reduced features.",
    "none": "No game-aware support; opens as a plain hex editor."
  },
  "roms": [
    { "game":"FireRed", "revision":"Rev 0 (v1.0)", "headerCode":"BPRE0",
      "noIntroName":"Pokemon - FireRed Version (USA).gba",
      "md5":"e26ee0d44e809351c8ce2d73c7400cdd",
      "sha1":"41CB23D8DCCC8EBD7C649CD8FBB58EEACE6E2FDC",
      "crc32":"DD88761C", "hmaSupport":"prime" },
    ... (the 9 bundled clean dumps: BPRE0, BPRE1, BPEE0, AXVE0, AXVE1, AXPE0, AXPE1, BPGE0, BPGE1)
  ],
  "alsoSupported": [],
  "notSupported": [
    { "headerCode":"AXVE2", "game":"Ruby Rev 2", "hmaSupport":"none" },
    { "headerCode":"AXPE2", "game":"Sapphire Rev 2", "hmaSupport":"none" },
    { "headerCodePrefix":"BPEF/BPEI/BPRF/...", "game":"Non-English R/S/E/FR/LG variants", "hmaSupport":"reduced" }
  ]
}
```

The 9 `roms` entries (all HMA-fully-supported revisions: FireRed 0/1, Emerald, Ruby 0/1,
Sapphire 0/1, LeafGreen 0/1 — with `BPRE0`/`BPEE0` flagged `prime`) use the verified
checksums recorded in `test/roms/PRISTINE-ROMS.md`. `.toml` MD5s are intentionally
excluded (HMA-version specific, not universal ROM identity). `alsoSupported` is empty
(all supported revisions are now bundled).

## 2. Loader — `SupportedRoms` (MCP)

Mirrors `Docs`: `SupportedRoms.Json()` returns the embedded JSON text (cached);
`SupportedRoms.All()` parses it (System.Text.Json) into the rom list;
`SupportedRoms.ByCode(code)` / `ByMd5(md5)` return a match or null. Missing-resource →
a clear sentinel (never throws).

## 3. Tool — `supported_roms(code = null)`

Returns the reference. No `code` → the whole document (text). With `code` (e.g.
`"BPRE0"` or `"bpre"`) → the matching entry/entries. Result `{ ok, text|matches, mode }`.
No ROM dependency (works live or headless). Tool count +1.

## 4. Resource — `hexmaniac://supported-roms`

Serves the embedded JSON (`application/json`), mirroring `hexmaniac://guide`.

## 5. Tool — `identify_rom(tab = null, tabFile = null)`

Identifies the **currently loaded/open** ROM. Both backends:
- Get the ROM bytes: headless `session.Require().RawData`; live the resolved tab's
  `vp.Model.RawData`.
- Read header: 4-char code at offset `0xAC` + version byte at `0xBC` → e.g. `BPRE0`.
- Compute `md5` (and `sha1`/`crc32`) of the bytes.
- Look up the reference:
  - `headerCode` known (in `roms`/`alsoSupported`/`notSupported`) → report `baseGame`,
    `revision`, `hmaSupport`.
  - `md5` matches a `roms` entry → `isCleanDump = true`, `matchedNoIntro = <name>`.
  - else → `isCleanDump = false` (edited/romhack); still report the base game from the
    header (this is the "what is this hack based on" answer).
- Result: `{ ok, headerCode, baseGame, revision, hmaSupport, isCleanDump, matchedNoIntro,
  md5, sha1, crc32, note, mode }`. Unknown header code → `baseGame:null` with a note
  ("unrecognized header code 'XXXX'; opens as plain hex"). `mode` stamped.

Note: hashes the **in-memory** ROM — unsaved edits make it read as non-clean (correct).

## 6. Docs

- New tracked `docs/SUPPORTED-ROMS.md` — human-readable rendering of the reference
  (table: game, revision, header code, No-Intro name, md5/sha1/crc32, support tier;
  plus the also/not-supported notes). Linked from `README.md`.
- `BUILD.md` + embedded `docs/MCP.md`: document `supported_roms` + `identify_rom` +
  the `hexmaniac://supported-roms` resource; bump tool counts.

## Components & boundaries

- **`supported-roms.json`** (resources/, tracked + embedded) — data.
- **`SupportedRoms`** (MCP) — loader/lookup.
- **`RomTools`** (MCP) — `supported_roms` + `identify_rom` tools; `IdentifyRomHeadless`.
- **`AutomationPipeServer`** (WPF) — `identify_rom` case (live).
- **`HelpResources`** (MCP) — add the `supported-roms` resource.
- No Core changes (header read + hashing done in the MCP layer over RawData).

## Error handling

`{ error }`, never crash: no ROM loaded / no tab; missing embedded resource (sentinel);
unknown header code (reported, not an error). Hashing failures surface a clear message.

## Testing (gate-safe)

Headless gate (`test/mcp-smoke.sh`, ALL GREEN; tool count +2 = 25):
- `supported_roms` (no arg) → text contains `BPRE0` and `FireRed`.
- `supported_roms {code:"BPEE0"}` → text contains `Emerald`.
- with the clean FireRed open: `identify_rom` → `isCleanDump:true`, `baseGame:"FireRed"`,
  `headerCode:"BPRE0"`, `matchedNoIntro` contains "FireRed".
- after a `write_value` edit (dirty/changed bytes): `identify_rom` → `isCleanDump:false`,
  still `baseGame:"FireRed"` (the "hack base" path).
- `resources/list` includes `hexmaniac://supported-roms`; `resources/read` non-empty JSON.
- Operate on the throwaway `$OR` copy; source ROM md5 unchanged.

## Out of scope

Hashes for non-bundled `alsoSupported` ROMs (BPRE1); deep romhack fingerprinting beyond
header-code base detection; non-Pokemon GBA games.
