# Pristine ROM reference (clean / vanilla / unedited)

This directory is the **reference point** for clean, unedited base games. When something
needs a "clean", "vanilla", or "unmodified" ROM (diffing a romhack, regenerating
metadata, a known-good baseline), use the copies here. Every ROM below is a verified
**No-Intro** dump, and each ships with an HMA-generated pristine `.toml` (+ a
`.toml.pristine.bak`).

> The machine-readable, repo-tracked source of truth (available to the MCP without this
> folder) is `src/HexManiac.Core/Models/Code/supported-roms.json`. This file is a local
> convenience copy.

## Support tiers (HexManiacAdvance)

- **★ Prime** — the primary, best-supported targets: **FireRed (USA) v1.0 `BPRE0`** and
  **Emerald (USA) `BPEE0`**. Most features, docs, and scripts assume these.
- **Full** — complete reference tables in HMA (`resources/tableReference.txt` /
  `HardcodeTablesModel`): Ruby/Sapphire/LeafGreen + their Rev 1s (and FireRed Rev 1).
  Fully editable, just more exotic — handy for diff dumps or romhacks that build on,
  say, Sapphire instead of the usual FireRed/Emerald.
- HMA does **not** support **Ruby/Sapphire Rev 2 (`AXVE2`/`AXPE2`)**, has only reduced
  default-metadata support for non-English variants (`BPEF`, `BPEI`, `BPRF`…), and opens
  anything else as a plain hex editor (the MCP `open_rom` metadata-guess warning).

HMA recognizes games by the **4-char header code + version byte** (e.g. `BPRE0`), not by
hash — so a romhack of a supported base still resolves to that base's code and stays
fully supported.

## The pristine set

| Game | Rev (plain) | Header | HMA | No-Intro filename |
|---|---|---|---|---|
| FireRed | v1.0 / Rev 0 | `BPRE0` | ★ Prime | `Pokemon - FireRed Version (USA).gba` |
| FireRed | v1.1 / Rev 1 | `BPRE1` | Full | `Pokemon - FireRed Version (USA, Europe) (Rev 1).gba` |
| Emerald | — | `BPEE0` | ★ Prime | `Pokemon - Emerald Version (USA, Europe).gba` |
| Ruby | v1.0 / Rev 0 | `AXVE0` | Full | `Pokemon - Ruby Version (USA, Europe).gba` |
| Ruby | v1.1 / Rev 1 | `AXVE1` | Full | `Pokemon - Ruby Version (USA, Europe) (Rev 1).gba` |
| Sapphire | v1.0 / Rev 0 | `AXPE0` | Full | `Pokemon - Sapphire Version (USA, Europe).gba` |
| Sapphire | v1.1 / Rev 1 | `AXPE1` | Full | `Pokemon - Sapphire Version (USA, Europe) (Rev 1).gba` |
| LeafGreen | v1.0 / Rev 0 | `BPGE0` | Full | `Pokemon - LeafGreen Version (USA).gba` |
| LeafGreen | v1.1 / Rev 1 | `BPGE1` | Full | `Pokemon - LeafGreen Version (USA, Europe) (Rev 1).gba` |

## ROM checksums

| Header | CRC32 | MD5 | SHA1 |
|---|---|---|---|
| `BPRE0` | `DD88761C` | `e26ee0d44e809351c8ce2d73c7400cdd` | `41CB23D8DCCC8EBD7C649CD8FBB58EEACE6E2FDC` |
| `BPRE1` | `84EE4776` | `51901a6e40661b3914aa333c802e24e8` | `DD5945DB9B930750CB39D00C84DA8571FEEBF417` |
| `BPEE0` | `1F1C08FB` | `605b89b67018abcea91e693a4dd25be3` | `F3AE088181BF583E55DAF962A92BB46F4F1D07B7` |
| `AXVE0` | `F0815EE7` | `53d1a2027ab49df34a689faa1fb14726` | `F28B6FFC97847E94A6C21A63CACF633EE5C8DF1E` |
| `AXVE1` | `61641576` | `e0503182a2e699678bcf25a6897a24d6` | `610B96A9C9A7D03D2BAFB655E7560CCFF1A6D894` |
| `AXPE0` | `554DEDC4` | `f34e91399c719812e66e2c828a2e93d7` | `3CCBBD45F8553C36463F13B938E833F652B793E4` |
| `AXPE1` | `BAFEDAE5` | `3a32fd98b065283d09eeba1ce0542888` | `4722EFB8CD45772CA32555B98FD3B9719F8E60A9` |
| `BPGE0` | `D69C96CC` | `612ca9473451fa42b51d1711031ed5f6` | `574FA542FFEBB14BE69902D1D36F1EC0A4AFD71E` |
| `BPGE1` | `DAFFECEC` | `9d33a02159e018d09073e700e1fd10fd` | `7862C67BDECBE21D1D69CE082CE34327E1C6ED5E` |

(SHA1/CRC32 are uppercase, no separators — exactly what HMA's `CalculateHashes` tool shows.)

## Pristine metadata (`.toml`) MD5

HMA-generated on a clean open (stable: identical on-open and on-close), one per ROM,
each mirrored to `<name>.toml.pristine.bak`. These are **HMA-version-specific** (they
change if HMA's metadata format changes), unlike the ROM hashes above which are universal.

| Header | `.toml` MD5 |
|---|---|
| `BPRE0` | `6536cf4b6bc5df9c8883772295124d71` |
| `BPRE1` | `f0f0a34ab8a0c4fdfc16cedd1a4f6bd7` |
| `BPEE0` | `7d9e8fd6f491e3abd3fd570e4820e03a` |
| `AXVE0` | `373c9a0c4a1a4008b8e6548e0d99010e` |
| `AXVE1` | `028d0dbb866798a01b409e5fe9597fa8` |
| `AXPE0` | `c44086841f35192084a16a479aba28b4` |
| `AXPE1` | `dad568fa50c0f0a8f280f55753f1ed69` |
| `BPGE0` | `774a5e9512750ec6d2f7671cf02506a4` |
| `BPGE1` | `d634b9c6400a2db8a2fa77ecede45f21` |

## Notes

- The smoke gates load `Pokemon - FireRed Version (USA).gba` as the test ROM (the old
  short-named `firered.gba` fixture was retired in favor of the canonical No-Intro name).
- Verify everything: `cd test/roms && md5sum -c pristine.md5sums.txt`
- All files here are gitignored (local only) — copyrighted ROMs/metadata never get
  committed; only the metadata *about* them (`supported-roms.json`) is tracked.
