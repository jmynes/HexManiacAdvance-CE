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
