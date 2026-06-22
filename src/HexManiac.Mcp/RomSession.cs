using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.Tools;

namespace HavenSoft.HexManiac.Mcp;

// Holds the single ROM/model that all MCP tools operate on.
// Registered as a DI singleton and injected into tool methods.
//
// We keep a full ViewPort (not just an IDataModel) because:
//   * edits need a real change token — Model.GetTableModel(name) alone uses a
//     NoDataChangeDeltaModel that throws on write; ViewPort.CurrentChange is a
//     real ModelDelta.
//   * running .hma scripts goes through ViewPort.TryImport / Edit.
public sealed class RomSession {
   public Singletons Singletons { get; } = new Singletons();
   public IFileSystem FileSystem { get; } = new StubFileSystem();

   public ViewPort? ViewPort { get; private set; }
   public IDataModel? Model => ViewPort?.Model;
   public string? RomPath { get; private set; }

   // Errors/messages raised by the ViewPort during the most recent operation.
   public List<string> Errors { get; } = new();
   public List<string> Messages { get; } = new();

   // Loads a .gba ROM into a fully-initialized model + headless ViewPort.
   public void Load(string path) => Load(path, new string[0]);

   public void Load(string path, string[] metadataLines) {
      if (!File.Exists(path)) throw new FileNotFoundException($"ROM not found: {path}");
      var data = File.ReadAllBytes(path);
      var model = new HardcodeTablesModel(Singletons, data, new StoredMetadata(metadataLines));
      model.InitializationWorkload?.Wait();
      var vp = new ViewPort(path, model, InstantDispatch.Instance, Singletons, new(), FileSystem);
      Errors.Clear();
      Messages.Clear();
      vp.OnError += (_, e) => Errors.Add(e);
      vp.OnMessage += (_, e) => Messages.Add(e);
      ViewPort = vp;
      RomPath = path;
   }

   // A real change token so edits actually modify model data.
   public ModelDelta Token => RequireViewPort().CurrentChange;

   public IDataModel Require() =>
      Model ?? throw new InvalidOperationException("No ROM loaded. Call open_rom with an absolute .gba path first.");

   public ViewPort RequireViewPort() =>
      ViewPort ?? throw new InvalidOperationException("No ROM loaded. Call open_rom with an absolute .gba path first.");

   // Lazily-built headless editor that hosts the Python engine. Mirrors PythonTests:
   // new EditorViewModel(fileSystem) + editor.Add(viewPort). Rebound when the ROM changes.
   private EditorViewModel? pythonEditor;
   private ViewPort? pythonEditorTab;
   private readonly List<string> pythonPrints = new();
   // The MCP host dispatches tool calls concurrently, but the Python engine (one CPython
   // scope + GIL, and a non-thread-safe EditorViewModel) is not concurrency-safe — two
   // overlapping run_python calls deadlock. Serialize all Python access here, the way the
   // live GUI path is serialized by marshalling onto the WPF UI thread.
   private readonly object pythonLock = new();

   private PythonTool EnsurePythonTool() {
      var vp = RequireViewPort();
      // Rebuild the whole headless editor when the ROM changes, rather than Add()-ing the new
      // tab onto the old one: EditorViewModel.Add never removes the prior tab, so re-opening
      // ROMs in one session would otherwise leak each stale ViewPort + model. The Python engine
      // is a process-wide singleton, so the fresh PythonTool reuses it (no re-init cost).
      if (pythonEditor == null || !ReferenceEquals(pythonEditorTab, vp)) {
         pythonEditor = new EditorViewModel(FileSystem, InstantDispatch.Instance, allowLoadingMetadata: false);
         pythonEditor.Add(vp);          // Add() sets SelectedIndex to this tab
         pythonEditorTab = vp;
      }
      var tool = pythonEditor.PythonTool;
      tool.PrintCapture = pythonPrints.Add;
      return tool;
   }

   public (bool ok, string? result, IReadOnlyList<string> prints, string? error) RunPython(string code) {
      lock (pythonLock) {
         var tool = EnsurePythonTool();
         pythonPrints.Clear();
         var info = tool.RunForAutomation(code);
         string? result = null, error = null;
         if (info.HasError && info.IsWarning) result = info.ErrorMessage;   // REPL value
         else if (info.HasError) error = info.ErrorMessage;                 // exception
         return (error == null, result, pythonPrints.ToList(), error);
      }
   }

   public object Introspect(string? target) {
      lock (pythonLock) {
         var model = Require();
         if (string.IsNullOrWhiteSpace(target)) return PythonIntrospection.Namespaces(model);
         var run = PythonIntrospection.ResolveTable(model, target);
         if (run != null) return PythonIntrospection.TableSchema(model, run, target);
         var tool = EnsurePythonTool();
         pythonPrints.Clear();
         return tool.DescribeExpression(target);
      }
   }
}
