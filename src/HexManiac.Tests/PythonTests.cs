using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System.Collections.Generic;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class PythonTests : BaseViewModelTestClass {
      private readonly PythonTool tool;
      private List<string> Prints { get; } = new();
      private string Execute(string code) {
         tool.Text = code;
         tool.RunPython();
         return tool.ResultText;
      }

      public PythonTests() {
         FileSystem.ShowCustomMessageBox = (text, _, _) => {
            Prints.Add(text);
            return true;
         };
         var editor = New.EditorViewModel();
         tool = new PythonTool(editor);
         editor.Add(ViewPort);
      }

      [Fact]
      public void SubtableWithLengthFromParent_EditFieldWithPython_SubtableLengthUpdated() {
         SetFullModel(0xFF);
         //              2         <child>     (1,2)        (3,4)
         ViewPort.Edit("02 00 00 00 <100> @100 01 00 02 00 03 00 04 00 ");
         ViewPort.Edit("@000 ^parent[length:: child<[x: y:]/length>]1 ");

         Execute("table['parent'][0].length = 3");

         var table = (ITableRun)Model.GetNextRun(0x100);
         Assert.Equal(3, table.ElementCount);
      }

      [Fact]
      public void TableWithTuple_AccessValueInPython_RightValue() {
         ViewPort.Edit("^elements[a:|t|x::|y::|z:: b:]2 (0 4 0) ");

         var result = Execute("table['elements'][0].a.y").Trim();

         Assert.Equal("4", result);
      }

      [Fact]
      public void TableWithTuple_SetValueInPython_RightValue() {
         ViewPort.Edit("^elements[a:|t|x::|y::|z:: b:]2 (0 4 0) ");

         Execute("table['elements'][0].a.y = 7").Trim();

         Assert.Equal(0x0070, Model.ReadMultiByteValue(0, 2));
      }

      // pythonnet uses .NET reflection, not the DLR DynamicObject protocol, so the
      // IronPython-era __len__/__getindex__ helpers were dropped. Confirm len(table)
      // and string-key column access still resolve through the .NET members.
      [Fact]
      public void Table_LenAndElementAccess_WorkUnderPythonnet() {
         ViewPort.Edit("^elements[a: b:]3 1 2 3 4 5 6 "); // rows: (1,2) (3,4) (5,6)

         Assert.Equal("3", Execute("len(table['elements'])").Trim()); // removed __len__ -> via .NET Count
         Assert.Equal("5", Execute("table['elements'][2].a").Trim()); // row 2, field a
      }

      [Fact]
      public void RunPython_EditsField_UndoRevertsRedoReapplies() {
         ViewPort.Edit("^elements[a:|t|x::|y::|z:: b:]2 (0 4 0) ");
         ViewPort.ChangeHistory.ChangeCompleted(); // close the setup edit as its own step, isolating the python run below

         Execute("table['elements'][0].a.y = 7");
         Assert.Equal(0x0070, Model.ReadMultiByteValue(0, 2));

         ViewPort.Undo.Execute();
         Assert.Equal(0x0040, Model.ReadMultiByteValue(0, 2));

         ViewPort.Redo.Execute();
         Assert.Equal(0x0070, Model.ReadMultiByteValue(0, 2));
      }

      [Fact]
      public void RunPython_TwoSeparateRuns_AreTwoSeparateUndoSteps() {
         ViewPort.Edit("^elements[a: b:]2 1 2 3 4 ");
         ViewPort.ChangeHistory.ChangeCompleted(); // close the setup edit as its own step, isolating the python runs below

         Execute("table['elements'][0].a = 10");
         Execute("table['elements'][0].b = 20");
         Assert.Equal(10, Model.ReadMultiByteValue(0, 2));
         Assert.Equal(20, Model.ReadMultiByteValue(2, 2));

         ViewPort.Undo.Execute(); // undoes only the second run
         Assert.Equal(10, Model.ReadMultiByteValue(0, 2));
         Assert.Equal(2, Model.ReadMultiByteValue(2, 2));

         ViewPort.Undo.Execute(); // undoes the first run
         Assert.Equal(1, Model.ReadMultiByteValue(0, 2));
         Assert.Equal(2, Model.ReadMultiByteValue(2, 2));
      }

      [Fact]
      public void GetAutocomplete_AnchorPrefix_SuggestsFullDottedAnchorName() {
         ViewPort.Edit("@000 ^data.pokemon.stats[hp: atk:]2 (10 20) (30 40) ");

         var options = tool.GetAutocomplete("dat", 0, 3);

         Assert.Contains(options, o => o.LineText == "data.pokemon.stats");
      }

      [Fact]
      public void GetAutocomplete_KeywordPrefix_SuggestsKeyword() {
         var options = tool.GetAutocomplete("imp", 0, 3);

         Assert.Contains(options, o => o.LineText == "import");
      }

      [Fact]
      public void GetAutocomplete_BuiltinPrefix_SuggestsBuiltinFunction() {
         var options = tool.GetAutocomplete("pri", 0, 3);

         Assert.Contains(options, o => o.LineText == "print");
      }

      [Fact]
      public void GetAutocomplete_ModulePrefix_SuggestsImportableModule() {
         // 'os' is always present (stdlib), unlike the pip-bootstrapped packages,
         // which are allowed to be missing - see BundledPackage_ImportRequests_Succeeds.
         var options = tool.GetAutocomplete("o", 0, 1);

         Assert.Contains(options, o => o.LineText == "os");
      }

      [Fact]
      public void GetAutocomplete_DottedPrefixAfterImport_SuggestsRealModuleMember() {
         Execute("import os");

         var options = tool.GetAutocomplete("os.path.j", 0, 9);

         Assert.Contains(options, o => o.LineText == "os.path.join");
      }

      [Fact]
      public void GetAutocomplete_InsideStringLiteral_ReturnsNull() {
         var options = tool.GetAutocomplete("print('pri", 0, 10);

         Assert.Null(options);
      }

      [SkippableFact]
      public void BundledPackage_ImportRequests_Succeeds() {
         // guards the pip-bootstrap pipeline in Directory.Build.targets: the embeddable
         // CPython package ships with site-packages disabled, so this only works if that
         // pipeline successfully enabled it and installed requests. That pipeline is
         // intentionally non-fatal (a network blip must never break the editor build), so
         // a missing package here is a real, expected possibility - skip rather than fail.
         var importResult = tool.RunPythonScript("import requests");
         Skip.If(importResult.HasError && !importResult.IsWarning, importResult.ErrorMessage);

         var result = Execute("import requests; 'requests imported ok'").Trim();
         Assert.Equal("requests imported ok", result);
      }
   }
}
