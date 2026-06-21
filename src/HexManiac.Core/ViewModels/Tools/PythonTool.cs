using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using Python.Runtime;
using System;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace HavenSoft.HexManiac.Core.ViewModels.Tools {
   public class PythonTool : ViewModelCore {
      private static readonly object engineLock = new();
      private static volatile bool engineInitialized;

      private readonly Lazy<PyModule> scope;
      private readonly EditorViewModel editor;
      private readonly TextEditorViewModel content;

      private string resultText;
      public string Text { get => content.Content; set => content.Content = value; }
      public string ResultText { get => resultText; set => Set(ref resultText, value); }
      public TextEditorViewModel PythonEditor => content;

      public PythonTool(EditorViewModel editor) {
         this.editor = editor;
         content = SetupPythonEditor();
         EnsureEngineInitialized();

         scope = new(() => {
            using (Py.GIL()) {
               var scope = Py.CreateScope();
               scope.Set("editor", editor);
               scope.Set("table", new TableGetter(editor));
               scope.Set("print", new Action<string>(Printer));
               try {
                  // 'ast' splits off a trailing expression statement so a script can mix
                  // statements (assignments, loops) with a final expression whose value gets
                  // returned, matching IronPython's ScriptEngine.Execute REPL-like behavior.
                  // Stringifying inside Python (rather than marshalling the raw value back to
                  // C#) sidesteps pythonnet's object-typed-parameter int/str marshalling quirks.
                  scope.Exec(@"
import clr
clr.AddReference('HexManiac.Core')
import HavenSoft.HexManiac.Core
from HavenSoft.HexManiac.Core.Models import IDataModel
import ast as __hma_ast__

def __hma_run__(__hma_code__):
   __hma_tree__ = __hma_ast__.parse(__hma_code__, mode='exec')
   __hma_value__ = None
   if __hma_tree__.body and isinstance(__hma_tree__.body[-1], __hma_ast__.Expr):
      __hma_last__ = __hma_ast__.Expression(__hma_tree__.body.pop().value)
      __hma_ast__.fix_missing_locations(__hma_last__)
      exec(compile(__hma_tree__, '<string>', 'exec'), globals())
      __hma_value__ = eval(compile(__hma_last__, '<string>', 'eval'), globals())
   else:
      exec(compile(__hma_tree__, '<string>', 'exec'), globals())
   if __hma_value__ is None:
      return None
   if isinstance(__hma_value__, str) or isinstance(__hma_value__, IDataModel):
      return str(__hma_value__)
   try:
      __hma_items__ = list(__hma_value__)
   except TypeError:
      return str(__hma_value__)
   return '\n'.join(str(__hma_item__) for __hma_item__ in __hma_items__)
");
                  scope.Exec(editor.Singletons.PythonUtility);
               } catch (Exception ex) {
                  Debug.Fail(ex.Message);
               }
               return scope;
            }
         });
         Text = @"print('''
   Put python code here.
   'editor' is the EditorViewModel.
   Tables from the current tab
     can be accessed by name.
   For example, try printing:
      data.pokemon.names[1].name
   Or try changing a table using a loop:

   for mon in data.pokemon.stats:
     mon.hp = 100
''')";
      }

      public void RunPython() {
         ResultText = RunPythonScript(Text).ErrorMessage ?? "null";
         // Closes this run's edits into their own undo/redo step (Ctrl+Z/Ctrl+Y), so they
         // don't bleed into whatever the user does next. RunPythonScript itself can't do
         // this - it's also used for read-only introspection (HasFunction/GetComment),
         // where forcibly completing some unrelated in-progress edit elsewhere would be wrong.
         if (editor.SelectedTab is IEditableViewPort vp) vp.ChangeHistory.ChangeCompleted();
         editor.SelectedTab?.Refresh();
      }

      public ErrorInfo RunPythonScript(string code) {
         using (Py.GIL()) {
            var scope = this.scope.Value;
            if (editor.SelectedTab is IEditableViewPort vp) {
               var anchors = AnchorGroup.GetTopLevelAnchorGroups(vp.Model, () => vp.ChangeHistory.CurrentChange);
               foreach (var key in anchors.Keys) scope.Set(key, anchors[key]);
            }
            try {
               scope.Set("__hma_code__", code);
               using var result = scope.Eval("__hma_run__(__hma_code__)");
               string resultText = result.IsNone() ? null : result.As<string>();
               if (resultText == null) return ErrorInfo.NoError;
               return new ErrorInfo(resultText, isWarningLevel: true);
            } catch (Exception ex) {
               return new ErrorInfo(ex.Message);
            }
         }
      }

      public bool HasFunction(string name) {
         var result = RunPythonScript($"'{name}' in globals()");
         return result.IsWarning && result.ErrorMessage == "True";
      }

      public string GetComment(string functionName) {
         var result = RunPythonScript($"{functionName}.__doc__");
         return result.IsWarning ? result.ErrorMessage.Trim() : null;
      }

      public void AddVariable(string name, object value) {
         using (Py.GIL()) scope.Value.Set(name, value);
      }

      public void Printer(string text) {
         editor.FileSystem.ShowCustomMessageBox(text, false);
      }

      private static void EnsureEngineInitialized() {
         if (engineInitialized) return;
         lock (engineLock) {
            if (engineInitialized) return;
            Runtime.PythonDLL = FindBundledPythonDll();
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();
            engineInitialized = true;
         }
      }

      private static string FindBundledPythonDll() {
         var arch = Environment.Is64BitProcess ? "x64" : "x86";
         var dir = Path.Combine(AppContext.BaseDirectory, "resources", "python", arch);
         var dll = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "python3*.dll").FirstOrDefault(f => Regex.IsMatch(Path.GetFileName(f), @"^python3\d\d\.dll$"))
            : null;
         if (dll == null) throw new InvalidOperationException($"No bundled Python runtime found under {dir}. Expected a python3xx.dll from the embeddable package.");
         return dll;
      }

      public static TextEditorViewModel SetupPythonEditor() {
         var editor = new TextEditorViewModel() {
            Keywords = {
               "for", "while", "in",
               "if", "elif", "else",
               "def", "return", "continue", "break", "yield",
               "class", "lambda",
               "import", "from",
               "try", "except",
               "with", "pass", "as", "is", "not",
               "len", "print", "zip", "range", "open", "round",
            },
            Constants = {
               "True",
               "False",
               "None",
               "str",
            },
            LineCommentHeader = "#",
            MultiLineCommentHeader = "'''",
            MultiLineCommentFooter = "'''",
            PreFormatter = new PythonTextFormatter(),
         };
         return editor;
      }

      public void Close() => editor.ShowAutomationPanel = false;
   }

   public class PythonTextFormatter : ITextPreProcessor {
      public TextFormatting[] Format(string content) {
         var result = new TextFormatting[content.Length];
         bool inSingleQuoteText = false;
         bool inDoubleQuoteText = false;
         var escaped = false;
         for (int i = 0; i < content.Length; i++) {
            var wasInQutoes = inSingleQuoteText || inDoubleQuoteText;
            if (!escaped && content[i] == '\'' && !inDoubleQuoteText) inSingleQuoteText = !inSingleQuoteText;
            if (!escaped && content[i] == '"' && !inSingleQuoteText) inDoubleQuoteText = !inDoubleQuoteText;
            if (wasInQutoes || inSingleQuoteText || inDoubleQuoteText) result[i] = TextFormatting.Text;
            escaped = content[i] == '\\' && !escaped;
         }
         return result;
      }
   }

   public record TableGetter(EditorViewModel Editor) {
      public DynamicObject this[string name] {
         get {
            if (Editor.SelectedTab is IViewPort viewPort && viewPort.Model is IDataModel model) {
               var address = model.GetAddressFromAnchor(new(), -1, name);
               var run = model.GetNextRun(address);
               ModelDelta factory() => viewPort.ChangeHistory.CurrentChange;
               if (run is EggMoveRun eggMoveRun) {
                  return new EggTable(model, factory, eggMoveRun);
               } else {
                  return new ModelTable(model, address, factory);
               }
            }
            return null;
         }
      }
   }
}
