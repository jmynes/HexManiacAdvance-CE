using System.Collections.Generic;
using HavenSoft.HexManiac.Core.Models;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class RomAutomationEditTests : BaseViewModelTestClass {
      // A table of 3 two-byte elements at 0x40.
      // ViewPort.Edit enters integer values as decimal, so 11/22/33 store as
      // decimal 11 (0x0B), 22 (0x16), 33 (0x21) in little-endian order.
      private void Setup() {
         ViewPort.Goto.Execute("40");
         ViewPort.Edit("^data[value:]3 11 22 33 ");  // elements: 11, 22, 33 (decimal)
      }

      [Fact] public void RowRange_ComputesAddressAndLength() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.RowRange(Model, "data", 1, 2);
         Assert.Equal(0x42, r["start"]);   // 0x40 + 1*2
         Assert.Equal(4, r["length"]);     // 2 elements * 2 bytes
      }

      [Fact] public void CopyRows_ReturnsHexBytes() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.CopyRows(Model, "data", 2, 1);
         Assert.Equal(true, r["ok"]);
         Assert.Equal("2100", r["bytes"]); // element 2 = 33 decimal = 0x21, little-endian bytes 21 00
      }

      [Fact] public void PasteRows_ClonesElement() {
         Setup();
         var copy = (Dictionary<string, object?>)RomAutomation.CopyRows(Model, "data", 0, 1); // 11 decimal -> "0B00"
         var paste = (Dictionary<string, object?>)RomAutomation.PasteRows(Model, () => Token, "data", 2, (string)copy["bytes"]);
         Assert.Equal(true, paste["ok"]);
         var read = (Dictionary<string, object?>)RomAutomation.ReadTable(Model, "data", 2, 1);
         var rows = (List<Dictionary<string, object?>>)read["rows"];
         Assert.Equal(11, rows[0]["value"]); // element 2 now equals element 0 (decimal 11)
      }

      [Fact] public void PasteRows_SizeMismatchReturnsError() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.PasteRows(Model, () => Token, "data", 0, "AABBCC"); // 3 bytes, elem len 2
         Assert.True(r.ContainsKey("error"));
      }

      [Fact] public void RowRange_OutOfRangeReturnsError() {
         Setup();
         var r = (Dictionary<string, object?>)RomAutomation.RowRange(Model, "data", 2, 5);
         Assert.True(r.ContainsKey("error"));
      }
   }
}
