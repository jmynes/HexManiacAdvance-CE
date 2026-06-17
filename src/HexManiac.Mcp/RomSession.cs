using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

// Holds the single ROM/model that all MCP tools operate on.
// Registered as a DI singleton and injected into tool methods.
public sealed class RomSession {
   public Singletons Singletons { get; } = new Singletons();

   public IDataModel? Model { get; private set; }
   public string? RomPath { get; private set; }

   // Loads a .gba ROM into a fully-initialized HexManiac model.
   public void Load(string path) {
      if (!File.Exists(path)) throw new FileNotFoundException($"ROM not found: {path}");
      var data = File.ReadAllBytes(path);
      var model = new HardcodeTablesModel(Singletons, data, new StoredMetadata(new string[0]));
      // The model does table auto-detection on a background task; wait for it.
      model.InitializationWorkload?.Wait();
      Model = model;
      RomPath = path;
   }

   // Returns the loaded model or throws a clear error for the client.
   public IDataModel Require() =>
      Model ?? throw new InvalidOperationException("No ROM loaded. Call open_rom with an absolute .gba path first.");
}
