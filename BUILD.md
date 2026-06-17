# Building this fork

This fork contains two things with **different .NET toolchain needs**, so there
are two build commands. This is intentional.

## 1. The HexManiacAdvance GUI editor (upstream)

Targets `net6.0-windows` and builds with the **.NET 6 SDK**, exactly like
upstream. The root `global.json` pins SDK 6, and the build must go through the
**solution** (the WPF XAML build relies on `$(SolutionDir)` and build order).

```bash
# from the repo root
dotnet build -c Release -p:PlatformTarget=x64
```

Output: `artifacts/HexManiac.WPF/bin/Release/net6.0-windows/HexManiacAdvance.exe`

Launch it by running that exe (double-click or from a terminal). Edit the C#/XAML
under `src/HexManiac.WPF` or `src/HexManiac.Core`, rebuild, relaunch.

> Note: building a single WPF `.csproj` directly fails (`MC3066`), and building
> the solution under SDK 8 fails (`CS0102` in the WPF temp project). Use the
> command above (SDK 6 + solution). That's why the MCP project is **not** part of
> `HexManiacAdvance.sln`.

## 2. The MCP server (this fork's addition)

Targets `net8.0` and builds with the **.NET 8 SDK**. It has its own
`src/HexManiac.Mcp/global.json` pinning SDK 8, so build it **from its own
directory** (or let the smoke test do it):

```bash
# from the repo root
( cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release )

# verify the server end-to-end (builds + drives it over stdio):
bash test/mcp-smoke.sh        # expect: ALL GREEN
```

Output: `src/HexManiac.Mcp/artifacts/HexManiac.Mcp/bin/Release/net8.0/HexManiac.Mcp.exe`
(this is the path `.mcp.json` points at). It's a stdio server — don't run it
directly; an MCP client (Claude Code) launches it. See `PROMPT.md` / the spec.

Both projects share the same `HexManiac.Core` (net6.0) library, which builds
fine under either SDK.
