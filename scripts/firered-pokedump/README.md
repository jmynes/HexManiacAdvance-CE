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

**National dex numbers** come from a stored resource, not the ROM: a species' *internal* index in
the ROM isn't its national dex number for Hoenn (Treecko is internal #277 but national #252), and the
ROM's `data.pokedex.*` tables are sort/pointer tables rather than a clean species→number map. The
canonical order lives in `src/HexManiac.Mcp/resources/national-pokedex.json` (PokeAPI national dex,
captured offline; mirrored into Core resources) and is matched to species by canonical `slug`.

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
- **Roaming beasts** (Raikou/Entei/Suicune): vanilla FRLG picks the roamer in an ASM starter-switch
  with no `setvar`, so the script walk can't see it. `build_dump.py` follows the `InitRoamer` special's
  handler pointer (`gSpecials[idx]`) and reads the species out of the `movs rX, #species` switch in
  that code. (CFRU-style hacks `setvar` it instead, caught by the walk.)
- **Curated** (`docs/firered-obtain-overrides.json`): now a **fallback only** — vanilla FireRed is
  fully ROM-derived with **zero** curated species.

Evolution is added **transitively** (a species counts only if a pre-evolution is itself reachable),
and respects FRLG reality: **trade evolutions** (Alakazam, Machamp, …) carry `evolvesFrom[].requiresTrade`,
and **Day/Night-friendship** evolutions (Espeon, Umbreon) are excluded — vanilla FRLG has no clock
(flip `HAS_DAY_NIGHT` in `build_dump.py` for a romhack that adds one).

## Choice locks & the four obtainability tiers

`choiceGroups` lists the mutually-exclusive "pick one" decisions. Each carries `oneSaveLock`: a **hard**
lock (starter line, Mt. Moon fossil, roaming beast) permanently forecloses its alternatives in a save;
a **soft** lock (the dojo Hitmons via Ditto→Tyrogue, the Eeveelutions via breeding) does not. The
top-level `obtainability` block reports four counts derived from this:

| tier | meaning | FireRed |
|------|---------|---------|
| `uniqueNoTrade` | distinct species on one cart, no link cable, any replays | 183 |
| `oneSaveNoTrade` | most obtainable in **one save** (hard locks cost their alternatives) | 173 |
| `soloNoTradeNoEvent` | …and no event distributions (drops Lugia/Ho-Oh/Deoxys tickets) | **170** |
| `uniqueSameVersionTrade` | + FireRed↔FireRed trade-evolutions | 192 |
| `crossVersionTradeCeiling` | National-Dex ceiling via LG/RSE/Colosseum trade (not ROM-derivable) | 383 |

`soloNoTradeNoEvent` (170) reproduces the [PokéCommunity solo-play completionist list](https://www.pokecommunity.com/showthread.php?t=374346)
species-for-species. Two FRLG quirks are handled so the count is honest: **Altering Cave**'s 8 Johto
wild slots are dead data (the released games lock the slot to Zubat; the alternates needed a Japan-only
e-Reader card), and the **ticket legendaries** (Lugia/Ho-Oh/Deoxys) carry `requiresEvent`. Each species
also has an `obtainableWithoutTrade` boolean.
