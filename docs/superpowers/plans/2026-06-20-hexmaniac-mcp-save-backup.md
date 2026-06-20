# MCP Save Backup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development (or executing-plans). Checkbox (`- [ ]`) steps.

**Goal:** Timestamped backups of the ROM (+ `.toml` + matching `.sav`) under a `backups/` folder — via a `backup_rom` tool and automatically before `save_rom` overwrites an existing file.

**Architecture:** A pure Core helper `RomBackup.Create(romPath, stamp)` copies the on-disk rom/toml/sav into `backups/<base>.<yyyyMMdd-HHmmss>.<ext>`. The `backup_rom` tool (both backends) and `save_rom`'s pre-overwrite auto-backup both call it.

**Tech Stack:** C# / .NET (Core net6.0, WPF net6.0, MCP net8.0), xUnit, bash + jq gate.

## Global Constraints
- Headless `test/mcp-smoke.sh` stays ALL GREEN. Tests must NOT modify `test/roms/firered.gba` (md5 `e26ee0d44e809351c8ce2d73c7400cdd`); operate on the throwaway `test/.tmp/firered.edited.gba` (`$OR`) so backups land in `test/.tmp/backups/`.
- Results carry `mode`. Reuse `Dispatch`/`Stamp`, `RomAutomation.Err`, `ResolveTab`/`NoTab`/`Ok`/`StrOrNull`/`Bool`, `GuiFileSystem`.
- Build: MCP `(cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release)`; GUI `dotnet build -c Release -p:PlatformTarget=x64`; tests `(cd src/HexManiac.Tests && dotnet test --filter ...)`. Headless gate with NO GUI.

---

## File Structure
- **Create `src/HexManiac.Core/Models/RomBackup.cs`** — the copy helper.
- **Create `src/HexManiac.Tests/RomBackupTests.cs`** — unit tests.
- **Modify `src/HexManiac.Mcp/RomTools.cs`** — `backup_rom` tool; `SaveRomHeadless` auto-backup.
- **Modify `src/HexManiac.WPF/AutomationPipeServer.cs`** — `backup_rom` case; `save_rom` auto-backup.
- **Modify `test/mcp-smoke.sh`** — backup_rom + save_rom backedUp assertions; tool count → 22.
- **Modify `BUILD.md`** — document backups.

---

## Task 1: Core `RomBackup.Create` + unit tests

**Files:** Create `src/HexManiac.Core/Models/RomBackup.cs`, `src/HexManiac.Tests/RomBackupTests.cs`.

**Interfaces:**
- Produces: `static IReadOnlyList<string> RomBackup.Create(string romPath, DateTime stamp)` (namespace `HavenSoft.HexManiac.Core.Models`).

- [ ] **Step 1: Write the failing tests.** Create `src/HexManiac.Tests/RomBackupTests.cs`:
```csharp
using System;
using System.IO;
using System.Linq;
using HavenSoft.HexManiac.Core.Models;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class RomBackupTests {
      private static string TempDir() {
         var d = Path.Combine(Path.GetTempPath(), "hma-backup-test-" + Guid.NewGuid().ToString("N"));
         Directory.CreateDirectory(d);
         return d;
      }
      [Fact] public void Create_CopiesRomTomlSav() {
         var dir = TempDir();
         var rom = Path.Combine(dir, "r.gba"); File.WriteAllBytes(rom, new byte[] { 1, 2, 3 });
         File.WriteAllText(Path.Combine(dir, "r.toml"), "meta");
         File.WriteAllBytes(Path.Combine(dir, "r.sav"), new byte[] { 9 });
         var stamp = new DateTime(2026, 6, 20, 14, 15, 30);
         var made = RomBackup.Create(rom, stamp);
         Assert.Equal(3, made.Count);
         Assert.True(File.Exists(Path.Combine(dir, "backups", "r.20260620-141530.gba")));
         Assert.True(File.Exists(Path.Combine(dir, "backups", "r.20260620-141530.toml")));
         Assert.True(File.Exists(Path.Combine(dir, "backups", "r.20260620-141530.sav")));
         Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(dir, "backups", "r.20260620-141530.gba")));
      }
      [Fact] public void Create_RomOnly_WhenNoSiblings() {
         var dir = TempDir();
         var rom = Path.Combine(dir, "x.gba"); File.WriteAllBytes(rom, new byte[] { 4 });
         var made = RomBackup.Create(rom, new DateTime(2026, 1, 2, 3, 4, 5));
         Assert.Single(made);
         Assert.True(File.Exists(Path.Combine(dir, "backups", "x.20260102-030405.gba")));
      }
      [Fact] public void Create_DistinctTimestamps_BuildTree() {
         var dir = TempDir();
         var rom = Path.Combine(dir, "r.gba"); File.WriteAllBytes(rom, new byte[] { 1 });
         RomBackup.Create(rom, new DateTime(2026, 6, 20, 1, 0, 0));
         RomBackup.Create(rom, new DateTime(2026, 6, 20, 2, 0, 0));
         Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "backups"), "r.*.gba").Length);
      }
      [Fact] public void Create_MissingRom_ReturnsEmpty() {
         var dir = TempDir();
         Assert.Empty(RomBackup.Create(Path.Combine(dir, "nope.gba"), DateTime.Now));
      }
   }
}
```

- [ ] **Step 2: Run the tests → RED.** `(cd src/HexManiac.Tests && dotnet test --filter "FullyQualifiedName~RomBackupTests")` — Expected: FAIL (RomBackup missing).

- [ ] **Step 3: Implement `src/HexManiac.Core/Models/RomBackup.cs`.**
```csharp
using System;
using System.Collections.Generic;
using System.IO;

namespace HavenSoft.HexManiac.Core.Models {
   // Copies a ROM and its sibling .toml / .sav (/.srm) into <dir>/backups/ as
   // <base>.<yyyyMMdd-HHmmss>.<ext>. Returns the created file paths.
   public static class RomBackup {
      public static IReadOnlyList<string> Create(string romPath, DateTime stamp) {
         var full = Path.GetFullPath(romPath);
         var dir = Path.GetDirectoryName(full) ?? ".";
         var baseName = Path.GetFileNameWithoutExtension(full);
         var ts = stamp.ToString("yyyyMMdd-HHmmss");
         var backupDir = Path.Combine(dir, "backups");
         var created = new List<string>();
         void CopyIfExists(string src, string ext) {
            if (!File.Exists(src)) return;
            Directory.CreateDirectory(backupDir);
            var dest = Path.Combine(backupDir, $"{baseName}.{ts}{ext}");
            File.Copy(src, dest, true);
            created.Add(dest);
         }
         CopyIfExists(full, Path.GetExtension(full));
         CopyIfExists(Path.Combine(dir, baseName + ".toml"), ".toml");
         CopyIfExists(Path.Combine(dir, baseName + ".sav"), ".sav");
         CopyIfExists(Path.Combine(dir, baseName + ".srm"), ".srm");
         return created;
      }
   }
}
```

- [ ] **Step 4: Run tests → GREEN** (4/4). Build MCP + GUI → 0 errors.

- [ ] **Step 5: Commit.**
```bash
git add src/HexManiac.Core/Models/RomBackup.cs src/HexManiac.Tests/RomBackupTests.cs
git commit -m "Core: RomBackup.Create (timestamped rom/toml/sav copy) + tests"
```

---

## Task 2: `backup_rom` tool + `save_rom` auto-backup

**Files:** Modify `src/HexManiac.Mcp/RomTools.cs`, `src/HexManiac.WPF/AutomationPipeServer.cs`, `test/mcp-smoke.sh`.

**Interfaces:**
- Consumes: `RomBackup.Create` (Task 1).
- Produces: `backup_rom(tab?, tabFile?)` tool + pipe method; `save_rom` results gain `backedUp: [paths]`.

- [ ] **Step 1: Write the failing tests — `test/mcp-smoke.sh`.** Bump tool count `21`→`22`. Add (operating on `$OR`, the throwaway copy). After the last driver line:
```bash
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":50,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":51,"method":"tools/call","params":{"name":"backup_rom","arguments":{}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":52,"method":"tools/call","params":{"name":"save_rom","arguments":{"overwrite":true}}}'; sleep 2
```
Assertions:
```bash
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "22" ] && ok "tools/list shows 22 tools" || bad "tools/list"
[ "$(result_text 51 | jq -r '.files | length' 2>/dev/null)" -ge 1 ] && ok "backup_rom created files" || bad "backup_rom files"
[ "$(result_text 52 | jq -r '.backedUp | length' 2>/dev/null)" -ge 1 ] && ok "save_rom overwrite auto-backs-up" || bad "save_rom backedUp"
```
(`$OR` = `test/.tmp/firered.edited.gba`; backups land in `test/.tmp/backups/`. The headless gate created `$OR` earlier via the save step; ensure id:50 opens it after it exists — it's created by the earlier id:6 save. If `$OR` may not exist at this point, add an `open_rom $R` + `save_rom outPath=$OR` just before; but id:6 already wrote `$OR`, so opening it is fine.)

- [ ] **Step 2: Run gate → RED.** `bash test/mcp-smoke.sh` (no GUI): `backup_rom` unknown (count 21≠22); `save_rom` result has no `backedUp`.

- [ ] **Step 3: Add the `backup_rom` MCP tool + auto-backup in `SaveRomHeadless` (`RomTools.cs`).**
```csharp
   [McpServerTool(Name = "backup_rom")]
   [Description("Make a timestamped backup of the ROM (plus its .toml and matching .sav, if present) under a backups/ folder next to it. Live targets the resolved tab; else headless.")]
   public string BackupRom(RomSession session,
      [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("backup_rom", new Dictionary<string, object?>(), tab, tabFile, () => {
         if (string.IsNullOrEmpty(session.RomPath)) return RomAutomation.Err("No ROM loaded to back up.");
         var stamp = System.DateTime.Now;
         var files = RomBackup.Create(session.RomPath, stamp);
         if (files.Count == 0) return RomAutomation.Err($"Nothing to back up (file not found: {session.RomPath}).");
         return new { ok = true, timestamp = stamp.ToString("yyyyMMdd-HHmmss"), files };
      });
   }
```
And update `SaveRomHeadless` to auto-backup an existing target (replace its body):
```csharp
   private static object SaveRomHeadless(RomSession session, string? outPath, bool overwrite) {
      var model = session.Require();
      session.RequireViewPort().ChangeHistory.ChangeCompleted();
      IReadOnlyList<string> Backup(string target) =>
         File.Exists(target) ? RomBackup.Create(target, System.DateTime.Now) : System.Array.Empty<string>();
      if (!string.IsNullOrEmpty(outPath)) {
         var backed = Backup(outPath);
         File.WriteAllBytes(outPath, model.RawData);
         return new { ok = true, path = outPath, overwrote = false, length = model.RawData.Length, backedUp = backed };
      }
      if (!overwrite)
         return RomAutomation.Err("Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source.");
      if (string.IsNullOrEmpty(session.RomPath)) return RomAutomation.Err("No loaded ROM path to overwrite.");
      var backed2 = Backup(session.RomPath);
      File.WriteAllBytes(session.RomPath, model.RawData);
      return new { ok = true, path = session.RomPath, overwrote = true, length = model.RawData.Length, backedUp = backed2 };
   }
```

- [ ] **Step 4: Add the `backup_rom` case + `save_rom` auto-backup in `AutomationPipeServer.cs`.** Add before `default:`:
```csharp
            case "backup_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var file = vp.FullFileName;
               if (string.IsNullOrEmpty(file)) return new AutoResponse(false, null, "This tab has no on-disk file to back up.");
               var stamp = System.DateTime.Now;
               var files = RomBackup.Create(file, stamp);
               if (files.Count == 0) return new AutoResponse(false, null, $"Nothing to back up (file not found: {file}).");
               return Ok(new { ok = true, timestamp = stamp.ToString("yyyyMMdd-HHmmss"), files });
            }
```
Replace the `save_rom` case body to auto-backup an existing target:
```csharp
            case "save_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var outPath = StrOrNull(p, "outPath");
               IReadOnlyList<string> Backup(string target) =>
                  !string.IsNullOrEmpty(target) && System.IO.File.Exists(target) ? RomBackup.Create(target, System.DateTime.Now) : System.Array.Empty<string>();
               if (!string.IsNullOrEmpty(outPath)) {
                  var backed = Backup(outPath);
                  System.IO.File.WriteAllBytes(outPath, vp.Model.RawData);
                  return Ok(new { ok = true, path = outPath, overwrote = false, length = vp.Model.RawData.Length, backedUp = backed });
               }
               if (!Bool(p, "overwrite", false))
                  return new AutoResponse(false, null, "Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source.");
               var backed2 = Backup(vp.FullFileName);
               vp.Save.Execute(GuiFileSystem());
               return Ok(new { ok = true, saved = vp.FullFileName ?? vp.Name, overwrote = true, length = vp.Model.RawData.Length, backedUp = backed2 });
            }
```
(`RomBackup` is in `HavenSoft.HexManiac.Core.Models`, already imported in both files. `System.Array.Empty<string>()` returns `string[]` which is `IReadOnlyList<string>`.)

- [ ] **Step 5: Build MCP + GUI → 0 errors.**

- [ ] **Step 6: Run headless gate.** `bash test/mcp-smoke.sh` (no GUI) → `ALL GREEN` incl. `tools/list shows 22 tools`, `backup_rom created files`, `save_rom overwrite auto-backs-up`. Then `md5sum test/roms/firered.gba` == `e26ee0d44e809351c8ce2d73c7400cdd`.

- [ ] **Step 7: Commit.**
```bash
git add src/HexManiac.Mcp/RomTools.cs src/HexManiac.WPF/AutomationPipeServer.cs test/mcp-smoke.sh
git commit -m "MCP backup_rom tool + save_rom auto-backup before overwrite"
```

---

## Task 3: Docs
**Files:** Modify `BUILD.md`, `docs/MCP.md`.
- [ ] **Step 1:** In `BUILD.md` (ROM/tab lifecycle) and `docs/MCP.md` (Saving & backups section + Tools index), document `backup_rom` and that `save_rom` auto-backs-up an existing target before overwriting (timestamped `backups/` of rom + toml + sav). Rebuild MCP so the embedded `docs/MCP.md` updates; run `bash test/mcp-smoke.sh` (no GUI) → ALL GREEN (the `help`/resource still serve, count stays 22).
- [ ] **Step 2:** `git add BUILD.md docs/MCP.md src/HexManiac.Mcp/* 2>/dev/null; git commit -m "Docs: backup_rom + save_rom auto-backup"` (include the embedded-doc rebuild only if a build artifact changed; normally just the two .md files).

---

## Self-Review notes
- **Spec coverage:** RomBackup.Create + tests (T1); backup_rom tool both backends (T2); save_rom auto-backup before overwriting an existing target, `backedUp` in result (T2); gates incl. md5 integrity; docs incl. embedded MCP.md (T3). Covered.
- **Headless invariant:** gate run T2 Step 6 + md5; tests target throwaway `$OR` → `test/.tmp/backups/`.
- **Type consistency:** `RomBackup.Create(string, DateTime) -> IReadOnlyList<string>` used by tool + both save paths; `backedUp` is that list; `System.Array.Empty<string>()` for the no-backup case. Tool count 21→22 (only `backup_rom` added).
- **Safety:** no test writes `test/roms/firered.gba`; backups go under `test/.tmp/`.
