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
