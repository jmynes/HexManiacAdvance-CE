using System.Linq;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class TrainerTeamExportTests : BaseViewModelTestClass {
      [Theory]
      [InlineData("MR. MIME", "MR. MIME")]
      [InlineData("NIDORAN\\sf", "NIDORAN♀")]
      [InlineData("NIDORAN\\sm", "NIDORAN♂")]
      [InlineData("\\pk\\mn TRAINER", "PKMN TRAINER")]
      public void ReadableText_RendersGlyphEscapes(string raw, string expected) =>
         Assert.Equal(expected, TrainerTeamExport.ReadableText(raw));

      // Build a tiny level-up learnset and confirm the default-moveset logic picks the
      // last <=4 moves learnable at or below the given level (padded to 4 with 0). Uses
      // the real ROM's child format: a [move: level.]!FFFF table-stream per species.
      private void ArrangeLearnset() {
         CreateTextTable(HardcodeTablesModel.PokemonNameTable, 0x100, "AAA", "BBB", "CCC", "DDD");
         CreateTextTable(HardcodeTablesModel.MoveNamesTable, 0x180,
            "NONE", "m1", "m2", "m3", "m4", "m5", "m6", "m7");
         // species 0 learns moves 1..7 at levels 1,1,7,13,20,25,33; species 1-3 learn nothing
         WriteLearnset(0x80, (1, 1), (2, 1), (3, 7), (4, 13), (5, 20), (6, 25), (7, 33));
         WriteLearnset(0xA0); // empty (just the terminator)
         ViewPort.Goto.Execute("000000");
         ViewPort.Edit("<000080><0000A0><0000A0><0000A0>");
         ViewPort.Goto.Execute("000000");
         ViewPort.Edit($"^{HardcodeTablesModel.LevelMovesTableName}[movesFromLevel<[move:{HardcodeTablesModel.MoveNamesTable} level.]!FFFF>]{HardcodeTablesModel.PokemonNameTable} ");
      }

      private void WriteLearnset(int start, params (int move, int level)[] entries) {
         int addr = start;
         foreach (var (move, level) in entries) {
            Model.WriteMultiByteValue(addr, 2, new ModelDelta(), move); addr += 2; // move (u16)
            Model[addr] = (byte)level; addr += 1;                                  // level (u8)
         }
         Model.WriteMultiByteValue(addr, 2, new ModelDelta(), 0xFFFF);             // terminator
      }

      [Fact] public void DefaultMoves_TakesLastFourAtOrBelowLevel() {
         ArrangeLearnset();
         var atL20 = TrainerPokemonTeamRun.GetDefaultMoves(Model, 0, 20);
         Assert.Equal(new[] { 2, 3, 4, 5 }, atL20.ToArray());  // 5 known (1..5), keep last 4
      }

      [Fact] public void DefaultMoves_PadsWhenFewerThanFour() {
         ArrangeLearnset();
         var atL5 = TrainerPokemonTeamRun.GetDefaultMoves(Model, 0, 5);
         Assert.Equal(new[] { 1, 2, 0, 0 }, atL5.ToArray());   // only moves 1 and 2 learned by L5
      }

      [Fact] public void DefaultMoves_EmptyLearnsetIsAllZero() {
         ArrangeLearnset();
         var none = TrainerPokemonTeamRun.GetDefaultMoves(Model, 1, 50);
         Assert.Equal(new[] { 0, 0, 0, 0 }, none.ToArray());
      }
   }
}
