using System.Collections.Generic;
using HavenSoft.HexManiac.Core.Models;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   // Species-indexed tables carry unused "limbo" slots (internal indices used for
   // Unown-variant graphics, not real species) whose species name is blank or only
   // '?'. RomAutomation excludes them from reads/exports by default.
   public class RomAutomationPlaceholderTests : BaseViewModelTestClass {
      // species 0/2 are real, species 1 is a placeholder ("?")
      private void SetupSpecies() {
         CreateTextTable(HardcodeTablesModel.PokemonNameTable, 0x100, "BULBASAUR", "?", "VENUSAUR");
         // a table whose length is tied to the species name table (species-indexed)
         ViewPort.Goto.Execute("40");
         ViewPort.Edit($"^data.test.stats[hp:]{HardcodeTablesModel.PokemonNameTable} 11 22 33 ");
      }

      private static List<Dictionary<string, object?>> Rows(object read) =>
         (List<Dictionary<string, object?>>)((Dictionary<string, object?>)read)["rows"];

      [Fact] public void SpeciesIndexedTable_OmitsPlaceholderByDefault() {
         SetupSpecies();
         var read = (Dictionary<string, object?>)RomAutomation.ReadTable(Model, "data.test.stats", 0, 25);
         var rows = Rows(read);
         Assert.Equal(2, rows.Count);                       // placeholder species 1 dropped
         Assert.DoesNotContain(rows, r => (int)r["index"]! == 1);
         Assert.Equal(1, read["excludedPlaceholders"]);
         Assert.True(read.ContainsKey("placeholderNote"));
      }

      [Fact] public void IncludePlaceholders_KeepsAllRows() {
         SetupSpecies();
         var read = (Dictionary<string, object?>)RomAutomation.ReadTable(Model, "data.test.stats", 0, 25, includePlaceholders: true);
         Assert.Equal(3, Rows(read).Count);
         Assert.False(read.ContainsKey("excludedPlaceholders"));
      }

      [Fact] public void TheSpeciesNameTableItself_OmitsPlaceholder() {
         SetupSpecies();
         var read = (Dictionary<string, object?>)RomAutomation.ReadTable(Model, HardcodeTablesModel.PokemonNameTable, 0, 25);
         var rows = Rows(read);
         Assert.Equal(2, rows.Count);
         Assert.Equal(1, read["excludedPlaceholders"]);
      }

      [Fact] public void NonSpeciesTable_IsUntouched() {
         // a plain table not tied to the species name table
         ViewPort.Goto.Execute("40");
         ViewPort.Edit("^data.plain[v:]3 11 22 33 ");
         var read = (Dictionary<string, object?>)RomAutomation.ReadTable(Model, "data.plain", 0, 25);
         Assert.Equal(3, Rows(read).Count);
         Assert.False(read.ContainsKey("excludedPlaceholders"));
      }
   }
}
