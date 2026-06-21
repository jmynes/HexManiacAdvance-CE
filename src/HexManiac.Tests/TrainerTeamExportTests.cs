using System.Linq;
using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Map;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class TrainerTeamExportTests : BaseViewModelTestClass {

      // Command address as bare uppercase hex, no 0x, no brackets, 6 digits min.
      [Theory]
      [InlineData(0x16A9F4, "16A9F4")]
      [InlineData(0x000100, "000100")]
      [InlineData(0x0, "000000")]
      [InlineData(0x1234567, "1234567")]
      public void FormatOffset_RendersBareHex(int address, string expected) =>
         Assert.Equal(expected, TrainerTeamExport.FormatOffset(address));

      // Verifies the load-bearing parts of the `uses` walk that don't need a full maps table:
      // locating a 0x5C trainerbattle site from a script start, reading its subtype + trainer id,
      // and walking its text-pointer args (start/playerwin) to decode the dialogue. This mirrors
      // exactly what CollectTrainerUses/ReadTextArgs do per spot.
      [Fact]
      public void TrainerBattleSpot_DecodesSubtypeTrainerAndDialogue() {
         SetFullModel(0xFF);
         // single.battle (subtype 0): 5C 00 <trainer:0001> <0000> start<100> playerwin<110> end
         ViewPort.Edit("5C 00 0100 0000 <100> <110> 02 @100 ^start\"\"Hello\" @110 ^win\"\"GG\"");
         ViewPort.CascadeScript(0);
         var parser = ViewPort.Tools.CodeTool.ScriptParser;

         var spots = Flags.GetAllScriptSpots(Model, parser, new[] { 0 }, 0x5C).ToList();
         var spot = Assert.Single(spots);

         Assert.Equal(0x00, Model[spot.Address + 1]);                               // subtype
         Assert.Equal(0x0001, Model.ReadMultiByteValue(spot.Address + 2, 2));       // trainer id

         // walk the args the same way ReadTextArgs does
         string intro = null, win = null;
         int offset = spot.Address + spot.Line.LineCode.Count;
         foreach (var arg in spot.Line.Args) {
            if (arg.PointerType == ExpectedPointerType.Text) {
               int dest = Model.ReadPointer(offset);
               int len = PCSString.ReadString(Model.RawData, dest, true);
               var text = Model.TextConverter.Convert(Model.RawData, dest, len)?.Trim('"');
               if (arg.Name == "start") intro = text;
               else if (arg.Name == "playerwin") win = text;
            }
            offset += arg.Length(Model, offset);
         }
         Assert.Equal("Hello", intro);
         Assert.Equal("GG", win);
      }
      [Theory]
      [InlineData("MR. MIME", "MR. MIME")]
      [InlineData("NIDORAN\\sf", "NIDORAN♀")]
      [InlineData("NIDORAN\\sm", "NIDORAN♂")]
      [InlineData("\\pk\\mn TRAINER", "PKMN TRAINER")]
      public void ReadableText_RendersGlyphEscapes(string raw, string expected) =>
         Assert.Equal(expected, TrainerTeamExport.ReadableText(raw));

      // Build tiny level-up learnsets using the real ROM child format (a
      // [move: level.]!FFFF table-stream per species). Species 0 deliberately
      // re-learns moves 3 and 4 at higher levels so we can exercise de-duplication.
      private void ArrangeLearnset() {
         CreateTextTable(HardcodeTablesModel.PokemonNameTable, 0x100, "AAA", "BBB", "CCC", "DDD");
         CreateTextTable(HardcodeTablesModel.MoveNamesTable, 0x180,
            "NONE", "m1", "m2", "m3", "m4", "m5", "m6", "m7");
         WriteLearnset(0x80, (1, 1), (2, 1), (3, 1), (4, 1), (3, 7), (4, 13), (5, 26)); // species 0 (dupes 3,4)
         WriteLearnset(0xA0, (6, 5));  // species 1: a single move
         WriteLearnset(0xB0);          // species 2/3: empty
         ViewPort.Goto.Execute("000000");
         ViewPort.Edit("<000080><0000A0><0000B0><0000B0>");
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

      // HMA's faithful in-game default moveset: FIFO by learnset order, keeps duplicates, pads to 4.
      [Fact] public void GetDefaultMoves_IsFaithfulFifoWithDuplicates() {
         ArrangeLearnset();
         Assert.Equal(new[] { 4, 3, 4, 5 }, TrainerPokemonTeamRun.GetDefaultMoves(Model, 0, 26).ToArray());
         Assert.Equal(new[] { 6, 0, 0, 0 }, TrainerPokemonTeamRun.GetDefaultMoves(Model, 1, 50).ToArray());
      }

      // The exporter's deduped variant: distinct moves, the 4 with the highest level, no padding.
      [Fact] public void DefaultMoveIds_DedupesAndTakesFourHighest() {
         ArrangeLearnset();
         // moves<=26 are {1@1,2@1,3@7,4@13,5@26}; the 4 highest distinct => 2,3,4,5 (no repeat of 3/4)
         Assert.Equal(new[] { 2, 3, 4, 5 }, TrainerTeamExport.DefaultMoveIds(Model, 0, 26).ToArray());
      }

      [Fact] public void DefaultMoveIds_FewerThanFourNotPadded() {
         ArrangeLearnset();
         Assert.Equal(new[] { 6 }, TrainerTeamExport.DefaultMoveIds(Model, 1, 50).ToArray()); // single move
         Assert.Empty(TrainerTeamExport.DefaultMoveIds(Model, 2, 50));                         // empty learnset
         Assert.Empty(TrainerTeamExport.DefaultMoveIds(Model, 0, 0));                          // nothing learned yet
      }
   }
}
