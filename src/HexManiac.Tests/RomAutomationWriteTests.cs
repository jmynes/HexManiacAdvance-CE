using System.Collections.Generic;
using HavenSoft.HexManiac.Core.Models;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class RomAutomationWriteTests : BaseViewModelTestClass {
      // Build: an enum option source `types`, then a `data` table with a PCS name,
      // an enum `kind` (-> types), an integer `power`, and a tuple-of-bits `flags` (a,b).
      private void Setup() {
         CreateTextTable("types", 0x00, "NORMAL", "FIGHTING", "FLYING");
         ViewPort.Goto.Execute("40");
         ViewPort.Edit("^data[name\"\"8 kind.types power. flags:|t|a:|b:]2 ");
      }
      private static Dictionary<string, object?> Write(BaseViewModelTestClass t, string field, object value, string flag = null)
         => (Dictionary<string, object?>)RomAutomation.WriteValue(t.Model, () => t.Token, "data", 0, field, value, flag);

      [Fact] public void SetsPcsString() {
         Setup();
         var r = Write(this, "name", "HELLO");
         Assert.Equal(true, r["ok"]); Assert.Equal("HELLO", r["newValue"]);
      }
      [Fact] public void SetsEnumByName() {
         Setup();
         var r = Write(this, "kind", "FLYING");
         Assert.Equal("FLYING", r["newValue"]);
      }
      [Fact] public void SetsInteger() {
         Setup();
         var r = Write(this, "power", 50);
         Assert.Equal(50, r["newValue"]);
      }
      [Fact] public void SetsFlag() {
         Setup();
         var r = Write(this, "flags", true, flag: "b");
         Assert.Equal(true, r["ok"]); Assert.Equal("b", r["flag"]); Assert.Equal(1, r["newValue"]);
      }
      [Fact] public void BadEnumReturnsError() {
         Setup();
         var r = Write(this, "kind", "NOPE");
         Assert.True(r.ContainsKey("error"));
         Assert.Contains("Options", (string)r["error"]);
      }
      [Fact] public void FlagOnNonBitArrayReturnsError() {
         Setup();
         var r = Write(this, "power", true, flag: "x");
         Assert.True(r.ContainsKey("error"));
      }
      [Fact] public void IntegerTypeMismatchReturnsError() {
         Setup();
         var r = Write(this, "power", "abc");
         Assert.True(r.ContainsKey("error"));
      }

      // The MCP client serializes the value argument as a JSON string (untyped
      // schema), so the engine must coerce a numeric/bool string to the field's type.
      [Fact] public void SetsIntegerFromNumericString() {
         Setup();
         var r = Write(this, "power", "120");
         Assert.Equal(true, r["ok"]); Assert.Equal(120, r["newValue"]);
      }
      [Fact] public void SetsFlagFromBoolString() {
         Setup();
         var r = Write(this, "flags", "true", flag: "b");
         Assert.Equal(true, r["ok"]); Assert.Equal(1, r["newValue"]);
      }
      [Fact] public void SetsEnumFromNumericString() {
         Setup();
         var r = Write(this, "kind", "2");
         Assert.Equal("FLYING", r["newValue"]);
      }
   }
}
