# Supported ROMs

HexManiacAdvance supports the five main English Pokémon GBA games (FireRed, LeafGreen, Emerald, Ruby, Sapphire) across their released revisions. The table below lists each ROM with its No-Intro filename and clean-dump checksums.

**How HMA identifies ROMs:** HexManiacAdvance recognises a ROM by the 4-character header game code and version byte encoded in the cartridge header (e.g. `BPRE0` for FireRed v1.0), **not** by hash. A romhack that is based on a supported game keeps that same code and stays fully supported regardless of what changes were made to the data.

> Source of truth: [`src/HexManiac.Mcp/resources/supported-roms.json`](../src/HexManiac.Mcp/resources/supported-roms.json).
> The MCP server exposes this data via the `supported_roms` tool, the `identify_rom` tool, and the `hexmaniac://supported-roms` resource.

---

## ROM table

| Game | Revision | Header code | No-Intro name | MD5 | SHA1 | CRC32 | HMA support |
|---|---|---|---|---|---|---|---|
| FireRed | Rev 0 (v1.0) | `BPRE0` | Pokemon - FireRed Version (USA).gba | `e26ee0d44e809351c8ce2d73c7400cdd` | `41CB23D8DCCC8EBD7C649CD8FBB58EEACE6E2FDC` | `DD88761C` | ★ Prime |
| FireRed | Rev 1 (v1.1) | `BPRE1` | Pokemon - FireRed Version (USA, Europe) (Rev 1).gba | `51901a6e40661b3914aa333c802e24e8` | `DD5945DB9B930750CB39D00C84DA8571FEEBF417` | `84EE4776` | Full |
| Emerald | Rev 0 | `BPEE0` | Pokemon - Emerald Version (USA, Europe).gba | `605b89b67018abcea91e693a4dd25be3` | `F3AE088181BF583E55DAF962A92BB46F4F1D07B7` | `1F1C08FB` | ★ Prime |
| Ruby | Rev 0 (v1.0) | `AXVE0` | Pokemon - Ruby Version (USA, Europe).gba | `53d1a2027ab49df34a689faa1fb14726` | `F28B6FFC97847E94A6C21A63CACF633EE5C8DF1E` | `F0815EE7` | Full |
| Ruby | Rev 1 (v1.1) | `AXVE1` | Pokemon - Ruby Version (USA, Europe) (Rev 1).gba | `e0503182a2e699678bcf25a6897a24d6` | `610B96A9C9A7D03D2BAFB655E7560CCFF1A6D894` | `61641576` | Full |
| Sapphire | Rev 0 (v1.0) | `AXPE0` | Pokemon - Sapphire Version (USA, Europe).gba | `f34e91399c719812e66e2c828a2e93d7` | `3CCBBD45F8553C36463F13B938E833F652B793E4` | `554DEDC4` | Full |
| Sapphire | Rev 1 (v1.1) | `AXPE1` | Pokemon - Sapphire Version (USA, Europe) (Rev 1).gba | `3a32fd98b065283d09eeba1ce0542888` | `4722EFB8CD45772CA32555B98FD3B9719F8E60A9` | `BAFEDAE5` | Full |
| LeafGreen | Rev 0 (v1.0) | `BPGE0` | Pokemon - LeafGreen Version (USA).gba | `612ca9473451fa42b51d1711031ed5f6` | `574FA542FFEBB14BE69902D1D36F1EC0A4AFD71E` | `D69C96CC` | Full |
| LeafGreen | Rev 1 (v1.1) | `BPGE1` | Pokemon - LeafGreen Version (USA, Europe) (Rev 1).gba | `9d33a02159e018d09073e700e1fd10fd` | `7862C67BDECBE21D1D69CE082CE34327E1C6ED5E` | `DAFFECEC` | Full |

---

## Support tiers

| Tier | Meaning |
|---|---|
| ★ Prime | Primary, best-supported targets. FireRed v1.0 (`BPRE0`) and Emerald (`BPEE0`) receive the most active testing and are the recommended bases for romhacks. |
| Full | Complete reference tables in HMA (`tableReference.txt` / `HardcodeTablesModel`). All standard editing features work. |
| Reduced | Default metadata only — opens and edits, but game-specific tables may be missing or incomplete. |
| None | No game-aware support. Opens as a plain hex editor. |

---

## Not supported

The following game codes are recognised but receive no full-featured support:

| Header code | Game | HMA support |
|---|---|---|
| `AXVE2` | Ruby Rev 2 | None |
| `AXPE2` | Sapphire Rev 2 | None |
| `BPEF0` / `BPEI0` / `BPRF0` / ... | Non-English language variants of R/S/E/FR/LG | Reduced |

Ruby Rev 2 (`AXVE2`) and Sapphire Rev 2 (`AXPE2`) are not commercially available in most regions and have no dedicated metadata in HMA.
