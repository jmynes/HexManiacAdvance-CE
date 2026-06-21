using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class StructRunTests : BaseViewModelTestClass {
      public StructRunTests() : base(0x100) { }

      private void SetupCardTables() {
         Model.SetList(Token, "cardnames", "BEW", "DDM", "BB1", "RA");
         ViewPort.Edit("@10 ^data.cards.passwords[password::|h]4 89631139 12345678 00000001 99999999 ");
      }

      [Fact]
      public void PointerWithBasicFields_NotWrappedInBrackets_CreatesStructRun() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 2); // mainCount
         Model.WriteMultiByteValue(0x4A, 2, Token, 0); // main[0] -> BEW
         Model.WriteMultiByteValue(0x4C, 2, Token, 1); // main[1] -> DDM
         Model.WriteMultiByteValue(0x4E, 2, Token, 1); // extraCount
         Model.WriteMultiByteValue(0x50, 2, Token, 2); // extra[0] -> BB1

         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("<040> ");
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("^data.decks.offsets.actual[offset<padding:: padding:: mainCount: " +
            "[main:cardnames|data.cards.passwords]/mainCount extraCount: " +
            "[extra:cardnames|data.cards.passwords]/extraCount>]1 ");

         Assert.Empty(Errors);
         var run = Model.GetNextRun(0x40);
         var structRun = Assert.IsType<StructRun>(run);
         Assert.Equal(18, structRun.Length); // 4+4+2+2+2+2+2+2
         Assert.Equal(7, structRun.ElementContent.Count);

         var names = new[] { "padding", "padding", "mainCount", "main", "main", "extraCount", "extra" };
         for (int i = 0; i < names.Length; i++) Assert.Equal(names[i], structRun.ElementContent[i].Name);
      }

      [Fact]
      public void InlineArray_EnumSegmentInsideStruct_ResolvesPasswordTableHint() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 1); // mainCount
         Model.WriteMultiByteValue(0x4A, 2, Token, 2); // main[0] -> BB1
         Model.WriteMultiByteValue(0x4C, 2, Token, 0); // extraCount

         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("<040> ");
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("^data.decks.offsets.actual[offset<padding:: padding:: mainCount: " +
            "[main:cardnames|data.cards.passwords]/mainCount extraCount: " +
            "[extra:cardnames|data.cards.passwords]/extraCount>]1 ");

         Assert.Empty(Errors);
         var structRun = (StructRun)Model.GetNextRun(0x40);
         var mainSegment = Assert.IsType<ArrayRunEnumSegment>(structRun.ElementContent[3]);
         Assert.Equal("cardnames", mainSegment.EnumName);
         Assert.Equal("data.cards.passwords", mainSegment.PasswordTableHint);
         Assert.Equal(4, mainSegment.GetOptions(Model).Count);
      }

      [Fact]
      public void InlineArray_ZeroCount_ProducesNoExpandedEntries() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 0); // mainCount = 0
         Model.WriteMultiByteValue(0x4A, 2, Token, 0); // extraCount = 0

         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("<040> ");
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("^data.decks.offsets.actual[offset<padding:: padding:: mainCount: " +
            "[main:cardnames|data.cards.passwords]/mainCount extraCount: " +
            "[extra:cardnames|data.cards.passwords]/extraCount>]1 ");

         Assert.Empty(Errors);
         var structRun = (StructRun)Model.GetNextRun(0x40);
         Assert.Equal(12, structRun.Length); // 4+4+2+2 only, no array entries
         Assert.Equal(4, structRun.ElementContent.Count);
      }

      [Fact]
      public void InlineArray_CountFieldAfterArray_ThrowsClearError() {
         // the count field must precede its array - there's no other way to know where an
         // inline (non-pointer) variable-length array's bytes end before reading its count.
         var ex = Assert.Throws<ArrayRunParseException>(() => {
            var fields = ArrayRun.ParseSegments("[main:data.cards.names]/mainCount mainCount:", Model);
            new StructRun(Model, 0x40, null, "x", fields);
         });
         Assert.Contains("mainCount", ex.Message);
      }

      [Fact]
      public void InlineArraySegment_InsideTopLevelArrayRun_ReturnsError() {
         // top-level tables have a fixed, statically-known ElementLength - inline variable-length
         // arrays are only meaningful inside a StructRun (a pointer's destination struct).
         var error = ArrayRun.TryParse(Model, "[mainCount: [main:data.cards.names]/mainCount]1", 0x40, null, out var _);
         Assert.True(error.HasError);
      }
   }
}
