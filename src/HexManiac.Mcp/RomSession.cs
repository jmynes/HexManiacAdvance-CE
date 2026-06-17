using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;

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
   public void Load(string path) {
      if (!File.Exists(path)) throw new FileNotFoundException($"ROM not found: {path}");
      var data = File.ReadAllBytes(path);
      var model = new HardcodeTablesModel(Singletons, data, new StoredMetadata(new string[0]));
      // Table auto-detection runs on a background task; wait for it.
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
}
