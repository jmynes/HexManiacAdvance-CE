# MCP Save Backup — Design Spec

**Date:** 2026-06-20
**Status:** Approved (user approved the design).
**Fork:** `Q:\Users\user\Projects\HexManiacAdvance-MCP` (branch `mcp-integration`)

## Goal

Keep a walkable tree of timestamped backups (ROM + its `.toml` + a matching save
file) so a bad edit/save is recoverable and diffable for debugging — via an
explicit `backup_rom` tool AND automatically before `save_rom` overwrites an
existing file.

## Core helper — `RomBackup.Create(string romPath, DateTime stamp)`

Pure, testable, in `HexManiac.Core` (namespace `HavenSoft.HexManiac.Core.Models`).
Copies the **on-disk** files into `<dir>/backups/` named `<base>.<yyyyMMdd-HHmmss>.<ext>`
(one timestamp per snapshot so the set sorts together):
- the ROM at `romPath` (e.g. `firered.20260620-141530.gba`),
- the sibling `<base>.toml` if it exists,
- a matching save: sibling `<base>.sav` if it exists (also `<base>.srm`).
Creates `backups/` if needed. Returns the list of created file paths. If a source
file is absent it's skipped; if `romPath` itself is absent, returns an empty list
(callers guard).

```csharp
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
      File.Copy(src, dest, overwrite: true);
      created.Add(dest);
   }
   CopyIfExists(full, Path.GetExtension(full));
   CopyIfExists(Path.Combine(dir, baseName + ".toml"), ".toml");
   CopyIfExists(Path.Combine(dir, baseName + ".sav"), ".sav");
   CopyIfExists(Path.Combine(dir, baseName + ".srm"), ".srm");
   return created;
}
```

## `backup_rom(tab = null, tabFile = null)` tool (live + headless)

Snapshots the resolved ROM's on-disk files via `RomBackup.Create(path, DateTime.Now)`.
- Headless: `path = session.RomPath` (error if none loaded).
- Live (pipe handler): `path = vp.FullFileName` (error if the tab has no file path).
Returns `{ ok: true, timestamp, files: [<created paths>], mode }`. If nothing was
created (rom path missing on disk) → a clear error.

## `save_rom` auto-backup before overwrite

Before `save_rom` writes to a target that **already exists on disk**, it first calls
`RomBackup.Create(target, DateTime.Now)`:
- Headless `SaveRomHeadless`: target = `outPath` (copy) or `session.RomPath`
  (overwrite). If `File.Exists(target)` → backup, then write.
- Live pipe `save_rom`: outPath branch → if `File.Exists(outPath)` backup then
  write; overwrite branch → backup `vp.FullFileName` (exists) then `vp.Save`.
The save result gains `backedUp: [<paths>]` (empty when the target didn't exist).
(If the target doesn't exist yet, there's nothing to back up — no backup made.)

## Wiring

- New pipe method `backup_rom` in `AutomationPipeServer` (resolve tab → file →
  `RomBackup.Create`). MCP `backup_rom` tool routes via `Dispatch` (headless lambda
  uses `session.RomPath`). `mode` stamped. Tool count 21 → 22.
- `save_rom` (both backends) calls `RomBackup.Create` before an overwriting write,
  threading the created paths into the result as `backedUp`.

## Error handling

`{ error }`, never crash: `backup_rom` with no loaded/open ROM or a tab with no
file; `RomBackup.Create` on a missing rom path returns empty → tool reports "nothing
to back up". File-copy errors surface as a clear message (the save still guards its
own errors).

## Testing (gate-safe — operate on the throwaway copy, never the real source)

- **Core unit tests** (`HexManiac.Tests`): make a temp dir with `r.gba` + `r.toml`
  + `r.sav`; `RomBackup.Create(rPath, fixedDateTime)` → assert `backups/r.<ts>.gba`,
  `.toml`, `.sav` all exist and byte-match the sources; a second call with a
  different stamp makes a distinct set (tree). A rom with no toml/sav → only the
  `.gba` copy.
- **Headless gate** (`test/mcp-smoke.sh`, ALL GREEN; tool count → 22): `open_rom`
  the throwaway `test/.tmp/firered.edited.gba`; `backup_rom` → `files` non-empty and
  the backup `.gba` exists under `test/.tmp/backups/`; `save_rom overwrite:true` on
  it → result `backedUp` non-empty and a backup exists. Confirm `test/roms/firered.gba`
  md5 unchanged (`e26ee0d44e809351c8ce2d73c7400cdd`).
- **Live gate:** optional `backup_rom` against the gate's open ROM asserting
  `mode:"live"` + files; keep it from overwriting the source (backup is copy-only,
  so it's safe).

## Components & boundaries

- **`RomBackup`** (Core) — the file-snapshot logic; pure, unit-tested.
- **`RomTools`** (MCP) — `backup_rom` tool; `SaveRomHeadless` auto-backup.
- **`AutomationPipeServer`** (WPF) — `backup_rom` case; `save_rom` auto-backup.

## Out of scope

Pruning/rotating old backups; backing up unsaved in-memory state (snapshots are of
on-disk files); restoring from a backup (a possible future `restore_backup` tool).
`backups/` directories are build/test artifacts — ensure the test ones land under
`test/.tmp/` (gitignored).
