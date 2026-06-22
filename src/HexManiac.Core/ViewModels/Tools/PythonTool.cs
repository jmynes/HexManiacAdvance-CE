using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using Python.Runtime;
using System;
using System.Collections.Generic;
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
import keyword as __hma_keyword__
import builtins as __hma_builtins_mod__
import pkgutil as __hma_pkgutil__

# precomputed once per scope for autocomplete: real keywords/builtins/importable
# module names from this exact interpreter, rather than a hand-maintained list.
__hma_keywords__ = list(__hma_keyword__.kwlist)
__hma_builtin_names__ = [__hma_n__ for __hma_n__ in dir(__hma_builtins_mod__) if not __hma_n__.startswith('_')]
__hma_module_names__ = sorted(set(__hma_n__ for _, __hma_n__, _ in __hma_pkgutil__.iter_modules()))

def __hma_dir__(expr, prefix):
   # used for 'installed pip module' attribute completion (e.g. requests.g -> get).
   # expr is a string, evaluated here (not by the caller) so a NameError from a module
   # that hasn't actually been imported yet this session is catchable - falling back to
   # importing it fresh (harmless: only populates sys.modules' cache, same as a real
   # import would, and doesn't touch the user's own globals()) lets completion work even
   # before the script's own `import requests` line has ever been run.
   try:
      __hma_obj__ = eval(expr, globals())
   except Exception:
      try:
         import importlib
         __hma_obj__ = importlib.import_module(expr)
      except Exception:
         return []
   try:
      return [__hma_n__ for __hma_n__ in dir(__hma_obj__) if __hma_n__.startswith(prefix) and not __hma_n__.startswith('_')]
   except Exception:
      return []

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

      private IReadOnlyList<string> keywordNames, builtinNames, moduleNames;

      // line/lineIndex/characterIndex match AutocompleteOverlay's Func<string,int,int,...>
      // contract (see CodeBody.GetTokenComplete for the established convention): lineIndex
      // is unused here since Python autocomplete only needs the current line's text.
      public IReadOnlyList<AutocompleteItem> GetAutocomplete(string line, int lineIndex, int characterIndex) {
         if (characterIndex < 0 || characterIndex > line.Length) return null;
         // don't offer completions inside a string literal
         if (InStringLiteral(line, characterIndex)) return null;

         var wordStart = characterIndex;
         while (wordStart > 0 && IsAutocompleteChar(line[wordStart - 1])) wordStart--;
         var prefix = line.Substring(wordStart, characterIndex - wordStart);
         if (prefix.Length == 0) return null;

         var before = line.Substring(0, wordStart);
         var after = line.Substring(characterIndex);

         IEnumerable<string> candidates;
         var dotIndex = prefix.LastIndexOf('.');
         if (dotIndex >= 0) {
            var objectExpr = prefix.Substring(0, dotIndex);
            var memberPrefix = prefix.Substring(dotIndex + 1);
            candidates = GetMemberNames(objectExpr, memberPrefix).Select(member => objectExpr + "." + member)
               .Concat(GetAnchorNames(prefix));
         } else {
            EnsureCompletionListsLoaded();
            candidates = GetAnchorNames(prefix)
               .Concat(keywordNames.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
               .Concat(builtinNames.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
               .Concat(moduleNames.Where(name => name.StartsWith(prefix, StringComparison.Ordinal)));
         }

         var results = candidates.Distinct().OrderBy(c => c, StringComparer.Ordinal).Take(50)
            .Select(candidate => new AutocompleteItem(candidate, before + candidate + after))
            .ToList();
         return results.Count > 0 ? results : null;
      }

      private static bool IsAutocompleteChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

      // Whether the cursor (just before characterIndex) sits inside a string literal, tracking
      // single- vs double-quote context and backslash escapes (same rules as
      // PythonTextFormatter.Format) so e.g. an apostrophe inside a "double-quoted" string
      // doesn't flip the state and wrongly suppress completion.
      private static bool InStringLiteral(string line, int characterIndex) {
         bool inSingle = false, inDouble = false, escaped = false;
         for (int i = 0; i < characterIndex && i < line.Length; i++) {
            if (!escaped && line[i] == '\'' && !inDouble) inSingle = !inSingle;
            if (!escaped && line[i] == '"' && !inSingle) inDouble = !inDouble;
            escaped = line[i] == '\\' && !escaped;
         }
         return inSingle || inDouble;
      }

      private IEnumerable<string> GetAnchorNames(string prefix) {
         if (editor.SelectedTab is not IViewPort vp || vp.Model is not IDataModel model) return Enumerable.Empty<string>();
         return model.Anchors.Where(anchor => anchor.StartsWith(prefix, StringComparison.Ordinal));
      }

      private IReadOnlyList<string> GetMemberNames(string objectExpr, string memberPrefix) {
         if (string.IsNullOrWhiteSpace(objectExpr)) return Array.Empty<string>();
         using (Py.GIL()) {
            try {
               // objectExpr/memberPrefix are passed as quoted string arguments (not interpolated
               // as bare source) so __hma_dir__ can catch a NameError - e.g. requests.g typed
               // before the script's own `import requests` line has run - itself and fall back
               // to importing the module fresh, instead of the whole Eval call throwing here
               // before __hma_dir__ is even entered.
               using var result = scope.Value.Eval($"__hma_dir__('{objectExpr}', '{memberPrefix}')");
               return result.As<string[]>() ?? Array.Empty<string>();
            } catch {
               return Array.Empty<string>();
            }
         }
      }

      private void EnsureCompletionListsLoaded() {
         if (keywordNames != null) return;
         using (Py.GIL()) {
            keywordNames = ReadGlobalStringList("__hma_keywords__");
            builtinNames = ReadGlobalStringList("__hma_builtin_names__");
            moduleNames = ReadGlobalStringList("__hma_module_names__");
         }
      }

      private IReadOnlyList<string> ReadGlobalStringList(string name) {
         try {
            using var value = scope.Value.Eval(name);
            return value.As<string[]>() ?? Array.Empty<string>();
         } catch {
            return Array.Empty<string>();
         }
      }

      // When non-null, print(...) output is routed here (automation/MCP capture) instead of
      // popping a message box. Null in normal GUI use, so the editor still shows a dialog.
      public Action<string> PrintCapture { get; set; }

      public void Printer(string text) {
         if (PrintCapture != null) { PrintCapture(text); return; }
         editor.FileSystem.ShowCustomMessageBox(text, false);
      }

      // Run code for automation (MCP / pipe): execute, then close this run's edits into a single
      // undo/redo step and refresh the tab - same commit boundary as the GUI's RunPython() button.
      // Returns the raw ErrorInfo so callers can split value (HasError && IsWarning) from exception.
      public ErrorInfo RunForAutomation(string code) {
         var info = RunPythonScript(code);
         if (editor.SelectedTab is IEditableViewPort vp) vp.ChangeHistory.ChangeCompleted();
         editor.SelectedTab?.Refresh();
         return info;
      }

      // Evaluate an arbitrary expression and report its stringified value plus its public dir()
      // members - the agent's equivalent of the GUI autocomplete dropdown. "Read-only" here is
      // behavioral, not structural: like RunPythonScript it never calls ChangeCompleted, so it
      // opens no undo step - but callers are expected to pass an expression, not an assignment
      // (a target like "data.x.y = 5" would still mutate the model without a commit boundary).
      public object DescribeExpression(string target) {
         var valueResult = RunPythonScript(target);
         if (valueResult.HasError && !valueResult.IsWarning)
            return new Dictionary<string, object> { ["error"] = $"Could not evaluate '{target}': {valueResult.ErrorMessage}" };
         var value = valueResult.IsWarning ? valueResult.ErrorMessage : "None";
         var dirResult = RunPythonScript($"sorted(n for n in dir({target}) if not n.startswith('_'))");
         var members = dirResult.IsWarning && dirResult.ErrorMessage != null
            ? dirResult.ErrorMessage.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
         return new Dictionary<string, object> {
            ["ok"] = true, ["kind"] = "value", ["target"] = target,
            ["members"] = members, ["value"] = value,
         };
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
