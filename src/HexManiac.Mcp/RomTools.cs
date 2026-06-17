using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using HavenSoft.HexManiac.Core.Models;
using ModelContextProtocol.Server;

namespace HavenSoft.HexManiac.Mcp;

// MCP tools for reading/editing GBA Pokémon ROM data via HexManiac.Core.
//
// The RomSession parameter is supplied by dependency injection (a registered
// singleton); the remaining parameters are the tool's arguments. Each tool
// returns a JSON string with a "mode" field ("headless" here; "live" once the
// GuiBridge is added). Shared table logic lives in HexManiac.Core/RomAutomation.
[McpServerToolType]
public sealed class RomTools {
   private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

   [McpServerTool(Name = "open_rom")]
   [Description("Load a GBA Pokémon ROM from an absolute file path (headless mode). Must be called before headless tools.")]
   public string OpenRom(RomSession session, [Description("Absolute path to a .gba ROM file")] string path) {
      session.Load(path);
      var model = session.Require();
      return Headless(new { ok = true, path, length = model.Count, anchorCount = model.Anchors.Count });
   }

   [McpServerTool(Name = "list_tables")]
   [Description("List the named data tables (anchors) in the loaded ROM. Optional case-insensitive substring filter.")]
   public string ListTables(RomSession session, [Description("Optional substring to filter anchor names")] string? filter = null) {
      return Headless(RomAutomation.ListTables(session.Require(), filter));
   }

   [McpServerTool(Name = "read_table")]
   [Description("Read a named table as JSON rows. Provide the anchor name (e.g. 'data.pokemon.stats'). Use start/count to page through large tables.")]
   public string ReadTable(
      RomSession session,
      [Description("Anchor/table name, e.g. data.pokemon.stats")] string name,
      [Description("First row index to return")] int start = 0,
      [Description("Maximum rows to return")] int count = 25) {
      return Headless(RomAutomation.ReadTable(session.Require(), name, start, count));
   }

   [McpServerTool(Name = "write_value")]
   [Description("Set a single integer field on a table row (in memory; call save_rom to persist). Returns old and new values.")]
   public string WriteValue(
      RomSession session,
      [Description("Anchor/table name")] string table,
      [Description("Row index")] int index,
      [Description("Field name within the row")] string field,
      [Description("New integer value")] int value) {
      return Headless(RomAutomation.WriteValue(session.Require(), () => session.Token, table, index, field, value));
   }

   [McpServerTool(Name = "export_table")]
   [Description("Export an entire table (all rows, no paging) to a JSON file on disk. Use for encounters, trainers, dex, etc.")]
   public string ExportTable(
      RomSession session,
      [Description("Anchor/table name to export")] string name,
      [Description("Absolute path of the .json file to write")] string outPath) {
      return Headless(RomAutomation.ExportToFile(session.Require(), name, outPath));
   }

   [McpServerTool(Name = "run_script")]
   [Description("Run an HMA script against the loaded ROM. Provide inline 'script' text OR 'path' to a .hma file. Returns ok + any errors/messages.")]
   public string RunScript(
      RomSession session,
      [Description("Inline HMA script text")] string script = "",
      [Description("Path to a .hma script file (takes precedence over inline text)")] string? path = null) {
      var vp = session.RequireViewPort();
      session.Errors.Clear();
      session.Messages.Clear();

      if (!string.IsNullOrEmpty(path)) {
         var full = Path.GetFullPath(path);
         if (!File.Exists(full)) return Headless(RomAutomation.Err($"Script file not found: {full}"));
         vp.TryImport(new LoadedFile(full, File.ReadAllBytes(full)), session.FileSystem);
      } else if (!string.IsNullOrEmpty(script)) {
         vp.Edit(script);
      } else {
         return Headless(RomAutomation.Err("Provide 'script' text or 'path' to a .hma file."));
      }

      vp.ChangeHistory.ChangeCompleted();
      var errors = session.Errors.ToList();
      return Headless(new { ok = errors.Count == 0, errors, messages = session.Messages.ToList() });
   }

   [McpServerTool(Name = "save_rom")]
   [Description("Write the (possibly edited) ROM to disk. Defaults to the loaded path; pass outPath to write a copy.")]
   public string SaveRom(RomSession session, [Description("Optional output path; defaults to the loaded ROM path")] string? outPath = null) {
      var model = session.Require();
      var target = string.IsNullOrEmpty(outPath) ? session.RomPath : outPath;
      if (string.IsNullOrEmpty(target)) return Headless(RomAutomation.Err("No output path and no loaded path to default to."));
      session.RequireViewPort().ChangeHistory.ChangeCompleted();
      File.WriteAllBytes(target, model.RawData);
      return Headless(new { ok = true, path = target, length = model.RawData.Length });
   }

   // ---- helpers ----

   // Serialize a result object and stamp mode:"headless".
   private static string Headless(object result) => Stamp(JsonSerializer.Serialize(result, Json), "headless");

   // Parse a JSON object string and add/overwrite a "mode" field.
   internal static string Stamp(string json, string mode) {
      var node = JsonNode.Parse(json)!.AsObject();
      node["mode"] = mode;
      return node.ToJsonString();
   }
}
