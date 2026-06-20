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
