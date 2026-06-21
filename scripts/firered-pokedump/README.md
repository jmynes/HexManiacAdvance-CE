# FireRed Poké-dump

Builds a single comprehensive JSON of every obtainable FireRed (BPRE0) Pokémon — stats, types,
abilities, full movesets, Pokédex flavor, evolutions, and **how each species is obtained** — by
combining HexManiacAdvance MCP exports with a small amount of ROM parsing.

Output: `output/firered-pokemon-full.json` (~2 MB, **gitignored** — it's a build artifact).

## Run it

```sh
# 1. regenerate the raw exports into output/_work/  (drives the built MCP exe)
python scripts/firered-pokedump/export_inputs.py

# 2. assemble the dump
python scripts/firered-pokedump/build_dump.py
```

### Prerequisites
- The MCP server built: `dotnet build src/HexManiac.Mcp/HexManiac.Mcp.csproj -c Release`
  (produces `artifacts/HexManiac.Mcp/bin/Release/net6.0/HexManiac.Mcp.exe`).
- The ROM at `test/roms/FireRed-netcompare.gba` (a clean FireRed 1.0).
- Python 3 (stdlib only).

`build_dump.py` reads the `output/_work/*.json` exports **and** parses the ROM directly for the
things that live behind pointers (level-up movesets, wild-encounter slots, TM/HM compatibility).

## Inputs (`output/_work/*.json`)

`export_inputs.py` produces all of these; 13 are plain table dumps, 4 are dedicated tools.

| file | source |
|------|--------|
| `names`, `stats`, `types`, `abilities`, `movenames`, `items`, `levelup`, `tms`, `tutors`, `evolutions`, `trades`, `wild`, `mapnames` | `export_table` of `data.pokemon.names` / `.stats` / `.type.names` / `data.abilities.names` / `data.pokemon.moves.names` / `data.items.stats` / `data.pokemon.moves.levelup` / `.tms` / `.tutors` / `data.pokemon.evolutions` / `data.pokemon.trades` / `data.pokemon.wild` / `data.maps.names` |
| `pokedex` | `export_pokedex` (category, height, weight, dex entry) |
| `script-encounters` | `export_script_encounters` (gift / static / legendary / roaming + trade NPC locations) |
| `species-sources` | `export_species_sources` (cross-references, incl. starters/trades/evolutions) |
| `coin-prizes` | `export_coin_prizes` (Game Corner species + coin cost) |

## Obtain methods

`obtainFromRomData` is everything derived from the ROM; `obtainCurated` is the residue that
genuinely can't be read from data. A species with **both empty** is truly unobtainable on a FireRed
cartridge (trade-/event-only) — counted in `gaps.unobtainableInFireRed`.

- **Read from the ROM:** wild (+ maps), gift / static / legendary (script walk), starter, in-game
  trade (species, nickname, held item, IVs, OT, **and map**), egg, breeding (derived), evolution,
  and Game-Corner cost. The script walk resolves commands/specials **by name**, so it also works on
  Emerald/romhacks. The uncatchable Pokémon-Tower ghost Marowak (`StartMarowakBattle`) is excluded.
- **Curated** (`docs/firered-obtain-overrides.json`): only the **roaming beast** remains — its species
  is chosen in ASM by your starter, with no literal anywhere in the script.
