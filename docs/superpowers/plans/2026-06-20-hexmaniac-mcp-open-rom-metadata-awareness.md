# `open_rom` Metadata Awareness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make `open_rom` warn (in the tool result) when asked to open a ROM that is not a recognized base game and has no sidecar `.toml` (so HexManiac would guess table offsets), offering the caller `find_toml` / `guess_offsets` (not implemented) / `guess`.

**Architecture:** All detection + branching lives in `RomTools.OpenRom` (game code via `GetGameCode`, recognized via `Singletons.GameReferenceTables`, sidecar via `<rom>.toml` existence). The actual open still routes through `Dispatch` (live new tab / headless `session.Load`); a new `RomSession.Load(path, metadataLines)` overload supports the `find_toml` headless path. No Core changes.

**Tech Stack:** C# / .NET (MCP net8.0, WPF/Core net6.0), `System.Text.Json`, bash + jq gates.

## Global Constraints

- Headless `test/mcp-smoke.sh` stays ALL GREEN; recognized games (firered) keep opening with no friction under `metadata="auto"`.
- No new MCP tool (open_rom is modified in place); tool count stays 20.
- Results carry `mode`; reuse `RomTools.Dispatch`/`Stamp`, `RomAutomation.Err`, `GuiBridge.IsGuiRunning()`.
- Detection: `gameCode = ((IReadOnlyList<byte>)File.ReadAllBytes(path)).GetGameCode()`; `recognized = session.Singletons.GameReferenceTables.TryGetValue(gameCode, out _)`; `hasToml = File.Exists(Path.ChangeExtension(path, ".toml"))`. (`GetGameCode` is in `HavenSoft.HexManiac.Core.Models`, already imported in RomTools.)
- Build: MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; GUI `dotnet build -c Release -p:PlatformTarget=x64`. Headless gate with NO GUI (`taskkill //F //IM HexManiacAdvance.exe`; `//IM HexManiac.Mcp.exe`).

---

## File Structure

- **Modify `src/HexManiac.Mcp/RomSession.cs`** — add `Load(string path, string[] metadataLines)`; make existing `Load(path)` delegate to it with `new string[0]`.
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — rewrite `OpenRom` with detection + `metadata` modes + `OpenProceed` helper.
- **Modify `test/mcp-smoke.sh`** — assert recognized firered, `guess_offsets` not-implemented, unrecognized warning, `guess` opens.
- **Modify `BUILD.md`** — document `open_rom` metadata modes.

---

## Task 1: `open_rom` metadata awareness

**Files:**
- Modify: `src/HexManiac.Mcp/RomSession.cs`
- Modify: `src/HexManiac.Mcp/RomTools.cs`
- Modify: `test/mcp-smoke.sh`

**Interfaces:**
- Consumes: `RomSession.Singletons` (`GameReferenceTables.TryGetValue`), `((IReadOnlyList<byte>)bytes).GetGameCode()`, `HardcodeTablesModel`, `StoredMetadata`, `Dispatch`, `Stamp`, `RomAutomation.Err`, `GuiBridge.IsGuiRunning()`, `JsonNode`.
- Produces: `open_rom(path, metadata="auto")` with modes `auto|find_toml|guess_offsets|guess`; `RomSession.Load(path, string[] metadataLines)`.

- [ ] **Step 1: Write the failing tests — extend `test/mcp-smoke.sh`.** Near the top of the driver section (after `R=...`/`OR=...` path setup, before the `{` heredoc), add an unrecognized fixture:

```bash
FAKE="$TMP/fakerom.gba"; cp "$ROM" "$FAKE"; printf 'ZZZZ' | dd of="$FAKE" bs=1 seek=172 count=4 conv=notrunc 2>/dev/null; rm -f "$TMP/fakerom.toml"
FAKEW="$(cygpath -m "$FAKE")"
```
(`0xAC = 172` is the GBA header game-code offset; overwriting it with `ZZZZ` makes the game unrecognized, and there is no `fakerom.toml`.)

Add driver calls inside the heredoc, after the last existing line:
```bash
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":36,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\",\"metadata\":\"guess_offsets\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":37,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$FAKEW\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":38,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$FAKEW\",\"metadata\":\"guess\"}}}"; sleep 8
```
Add assertions (after the existing ones). Note `result_text 3` is the existing first `open_rom` of firered (auto):
```bash
# open_rom metadata awareness
[ "$(result_text 3 | jq -r '.recognized' 2>/dev/null)" = "true" ] && ok "open_rom reports recognized firered" || bad "open_rom recognized"
[ -n "$(result_text 3 | jq -r '.gameCode // empty' 2>/dev/null)" ] && ok "open_rom reports gameCode" || bad "open_rom gameCode"
[ "$(result_text 36 | jq -r '.error' 2>/dev/null | grep -ci 'not yet implemented')" -ge 1 ] && ok "guess_offsets not-implemented notice" || bad "guess_offsets notice"
[ "$(result_text 37 | jq -r '.needsMetadataChoice' 2>/dev/null)" = "true" ] && ok "unrecognized auto -> needsMetadataChoice" || bad "unrecognized warning"
[ "$(result_text 38 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "guess opens unrecognized rom" || bad "guess open"
```
Note: id:38 reopens (guess) the fake ROM into the session; it is the last open in the run, so it does not disturb earlier assertions (they read captured output by id).

- [ ] **Step 2: Run the gate to verify it fails.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: FAIL — `open_rom` ignores `metadata`, returns no `recognized`/`needsMetadataChoice`/`gameCode`, and `guess_offsets` opens instead of the notice.

- [ ] **Step 3: Add the `RomSession.Load` overload.** In `src/HexManiac.Mcp/RomSession.cs`, replace `Load(string path)` with:

```csharp
   public void Load(string path) => Load(path, new string[0]);

   public void Load(string path, string[] metadataLines) {
      if (!File.Exists(path)) throw new FileNotFoundException($"ROM not found: {path}");
      var data = File.ReadAllBytes(path);
      var model = new HardcodeTablesModel(Singletons, data, new StoredMetadata(metadataLines));
      model.InitializationWorkload?.Wait();
      var vp = new ViewPort(path, model, InstantDispatch.Instance, Singletons, new(), FileSystem);
      Errors.Clear();
      Messages.Clear();
      vp.OnError += (_, e) => Errors.Add(e);
      vp.OnMessage += (_, e) => Messages.Add(e);
      ViewPort = vp;
      RomPath = path;
   }
```

- [ ] **Step 4: Rewrite `OpenRom` + add `OpenProceed`.** In `src/HexManiac.Mcp/RomTools.cs`, replace the `OpenRom` method with:

```csharp
   [McpServerTool(Name = "open_rom")]
   [Description("Open a GBA ROM. Live: new GUI tab; headless: single session. For ROMs that are NOT a recognized base game (FireRed/Emerald/...) AND have no sidecar .toml, HexManiac guesses table offsets; under metadata='auto' this returns a warning with choices instead of opening. metadata: 'auto' (default) | 'find_toml' | 'guess_offsets' | 'guess'.")]
   public string OpenRom(RomSession session,
      [Description("Absolute path to a .gba ROM file")] string path,
      [Description("auto | find_toml | guess_offsets | guess")] string metadata = "auto") {
      string mode = GuiBridge.IsGuiRunning() ? "live" : "headless";
      string err(string m) => Stamp(JsonSerializer.Serialize(RomAutomation.Err(m), Json), mode);
      if (!File.Exists(path)) return err($"ROM not found: {path}");
      string gameCode;
      try { gameCode = ((IReadOnlyList<byte>)File.ReadAllBytes(path)).GetGameCode(); }
      catch (System.Exception ex) { return err($"Could not read ROM header: {ex.Message}"); }
      bool recognized = session.Singletons.GameReferenceTables.TryGetValue(gameCode, out _);
      var tomlPath = Path.ChangeExtension(path, ".toml");
      bool hasToml = File.Exists(tomlPath);

      switch (metadata) {
         case "guess_offsets":
            return err("Auto-detecting table offsets for an unrecognized ROM is not yet implemented. Please bug jmynes on GitHub or jordank.memes on Discord for this feature.");
         case "find_toml":
            if (!hasToml) return err($"No sidecar .toml found next to '{path}' (looked for '{tomlPath}'). Use metadata='guess' to open with guessed offsets, or metadata='guess_offsets'.");
            return OpenProceed(session, path, gameCode, recognized, "toml", File.ReadAllLines(tomlPath), false);
         case "guess":
            return OpenProceed(session, path, gameCode, recognized, recognized ? "builtin" : "guessed", hasToml ? File.ReadAllLines(tomlPath) : null, warnGuess: !recognized && !hasToml);
         case "auto":
         default:
            if (recognized || hasToml)
               return OpenProceed(session, path, gameCode, recognized, hasToml ? "toml" : "builtin", hasToml ? File.ReadAllLines(tomlPath) : null, false);
            var warn = new Dictionary<string, object?> {
               ["ok"] = false, ["needsMetadataChoice"] = true, ["gameCode"] = gameCode,
               ["recognized"] = false, ["hasToml"] = false,
               ["warning"] = $"'{path}' is not a recognized base game (e.g. FireRed/Emerald) and has no sidecar .toml, so HexManiac would guess table offsets — reads and writes may be inaccurate.",
               ["options"] = new Dictionary<string, object?> {
                  ["find_toml"] = "Re-call open_rom with metadata='find_toml' to use a .toml placed next to the ROM.",
                  ["guess_offsets"] = "Re-call with metadata='guess_offsets' to auto-detect offsets (not yet implemented).",
                  ["guess"] = "Re-call with metadata='guess' to open anyway with guessed metadata.",
               },
            };
            return Stamp(JsonSerializer.Serialize(warn, Json), mode);
      }
   }

   private static string OpenProceed(RomSession session, string path, string gameCode, bool recognized, string metadataSource, string[]? tomlLines, bool warnGuess) {
      var p = new Dictionary<string, object?> { ["path"] = path };
      var json = Dispatch("open_rom", p, null, null, () => {
         if (tomlLines != null) session.Load(path, tomlLines); else session.Load(path);
         var model = session.Require();
         return new { ok = true, path, length = model.Count, anchorCount = model.Anchors.Count };
      });
      var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
      node["gameCode"] = gameCode;
      node["recognized"] = recognized;
      node["metadataSource"] = metadataSource;
      if (warnGuess) node["warning"] = "Opened with guessed metadata — this ROM is not a recognized base game and has no sidecar .toml; offsets may be inaccurate.";
      return node.ToJsonString();
   }
```

(`System.Collections.Generic`, `System.Text.Json`, `System.Text.Json.Nodes`, `HavenSoft.HexManiac.Core.Models` are already imported in RomTools. `GameReferenceTables.TryGetValue(string, out ...)` mirrors `HardcodeTablesModel.cs:153`; if the out type isn't inferable, use `out var _tables`.)

- [ ] **Step 5: Build MCP + GUI.** `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)` and `dotnet build -c Release -p:PlatformTarget=x64`. Expected: 0 errors.

- [ ] **Step 6: Run the gate to verify it passes.** Run: `bash test/mcp-smoke.sh` (no GUI) — Expected: `ALL GREEN`, including the 5 new assertions; all prior assertions still pass (firered is recognized → opens under auto exactly as before, result now also carrying `recognized`/`gameCode`).

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomSession.cs src/HexManiac.Mcp/RomTools.cs test/mcp-smoke.sh
git commit -m "open_rom: warn on guessed metadata for unrecognized ROMs (find_toml/guess_offsets/guess)"
```

---

## Task 2: Docs

**Files:**
- Modify: `BUILD.md`

- [ ] **Step 1: Document the modes.** In `BUILD.md`, under the "### ROM/tab lifecycle" subsection, update the `open_rom` bullet (or add beneath it):

```markdown
- `open_rom` also reports `gameCode`/`recognized`/`metadataSource`. For a ROM that
  is NOT a recognized base game (FireRed/Emerald/...) and has no sidecar `.toml`,
  `metadata='auto'` (default) returns a `needsMetadataChoice` warning instead of
  opening — re-call with `metadata='find_toml'` (use a `.toml` next to the ROM),
  `metadata='guess'` (open with guessed offsets anyway), or `metadata='guess_offsets'`
  (auto-detect — not yet implemented).
```

- [ ] **Step 2: Commit.**
```bash
git add BUILD.md
git commit -m "Docs: open_rom metadata modes in BUILD.md"
```

---

## Self-Review notes

- **Spec coverage:** detection (gameCode/recognized/hasToml) in OpenRom (T1); auto warns on unrecognized+no-toml, opens otherwise (T1); find_toml uses sidecar or errors (T1); guess_offsets not-implemented notice (T1); guess opens + warns (T1); RomSession.Load(toml) for headless find_toml (T1); recognized fields on result (T1 OpenProceed); gates (T1); docs (T2). All covered.
- **Headless invariant:** recognized firered opens under auto unchanged; gate run in T1 Step 6.
- **Type consistency:** `OpenProceed(session, path, gameCode, recognized, metadataSource, tomlLines, warnGuess)` used by all proceed paths; `RomSession.Load(path)`/`Load(path, lines)` both defined; `err(...)`/`mode` local helpers. No new tool (count 20).
- **Test fixture note:** the unrecognized ROM is firered copied to `$TMP/fakerom.gba` with its game-code bytes (offset 172) overwritten to `ZZZZ` and no sidecar toml — deterministic; `guess` still loads it (empty metadata) so `ok:true`.
