# Refactor Report: SupportedRoms moved to Core

## Files moved
- `src/HexManiac.Mcp/resources/supported-roms.json` → `src/HexManiac.Core/Models/Code/supported-roms.json`
- `src/HexManiac.Mcp/SupportedRoms.cs` → `src/HexManiac.Core/Models/SupportedRoms.cs`

## Namespace change
`HavenSoft.HexManiac.Mcp` → `HavenSoft.HexManiac.Core.Models` in `SupportedRoms.cs`.

## csproj changes
- `src/HexManiac.Core/HexManiac.Core.csproj`: added `<EmbeddedResource Include="Models\Code\supported-roms.json" />` (single tracked embed)
- `src/HexManiac.Mcp/HexManiac.Mcp.csproj`: removed `<EmbeddedResource Include="resources\supported-roms.json" LogicalName="HexManiac.Mcp.supported-roms.json" />`
- `src/HexManiac.WPF/HexManiac.WPF.csproj`: removed `<EmbeddedResource Include="$(SolutionDir)src\HexManiac.Mcp\resources\supported-roms.json" LogicalName="HexManiac.Mcp.supported-roms.json" />`

## WPF changes
- `src/HexManiac.WPF/AutomationPipeServer.cs`: replaced `IdentifyMatch(code, md5, sha1, crc32)` call with `SupportedRoms.Match(code, md5, sha1, crc32)` (Core's); deleted the entire private `IdentifyMatch` method (~34 lines removed).

## MCP changes
- `src/HexManiac.Mcp/HelpResources.cs`: added `using HavenSoft.HexManiac.Core.Models;`
- `src/HexManiac.Mcp/RomTools.cs`: already had `using HavenSoft.HexManiac.Core.Models;` — no change needed.

## Docs path updates
- `docs/SUPPORTED-ROMS.md` line 7: updated path to `src/HexManiac.Core/Models/Code/supported-roms.json`
- `test/roms/PRISTINE-ROMS.md` line 10: updated path to `src/HexManiac.Core/Models/Code/supported-roms.json`

## Grep-clean confirmations
- `grep -rn "IdentifyMatch" src/` → **NOTHING** (duplicate gone)
- `grep -n "supported-roms" src/HexManiac.Mcp/HexManiac.Mcp.csproj` → **NOTHING**
- `grep -n "supported-roms" src/HexManiac.WPF/HexManiac.WPF.csproj` → **NOTHING**
- `grep -n "supported-roms" src/HexManiac.Core/HexManiac.Core.csproj` → line 100: single EmbeddedResource only

## Build results
- `dotnet build -c Release -p:PlatformTarget=x64` → `0 Error(s)` (16 pre-existing warnings, unchanged)
- `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` → `0 Error(s)` (11 warnings, unchanged)

## Gate result
`bash test/mcp-smoke.sh` → `PASS=56 FAIL=0 ALL GREEN`

## ROM md5 verified
`md5sum "test/roms/Pokemon - FireRed Version (USA).gba"` → `e26ee0d44e809351c8ce2d73c7400cdd` ✓

## Concerns
None. `Assembly.GetExecutingAssembly()` in Core's `SupportedRoms.Json()` correctly resolves to the Core assembly where the JSON is embedded. The `EndsWith("supported-roms.json")` loader pattern is assembly-agnostic.
