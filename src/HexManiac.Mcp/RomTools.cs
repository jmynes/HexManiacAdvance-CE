using System.ComponentModel;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using ModelContextProtocol.Server;

namespace HavenSoft.HexManiac.Mcp;

// MCP tools for reading/editing GBA Pokémon ROM data via HexManiac.Core.
//
// The RomSession parameter is supplied by dependency injection (it is a
// registered singleton); the remaining parameters are the tool's arguments.
//
// IMPLEMENTED below: open_rom, list_tables, read_table.
// STUBS for the Ralph loop to implement: write_value, export_table,
// run_script, save_rom. See PROMPT.md and docs/ for the intended behavior.
[McpServerToolType]
public sealed class RomTools {
   private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

   [McpServerTool(Name = "open_rom")]
   [Description("Load a GBA Pokémon ROM from an absolute file path. Must be called before any other tool.")]
   public string OpenRom(RomSession session, [Description("Absolute path to a .gba ROM file")] string path) {
      session.Load(path);
      var model = session.Require();
      return JsonSerializer.Serialize(new {
         ok = true,
         path,
         length = model.Count,
         anchorCount = model.Anchors.Count,
      }, Json);
   }

   [McpServerTool(Name = "list_tables")]
   [Description("List the named data tables (anchors) in the loaded ROM. Optional case-insensitive substring filter.")]
   public string ListTables(RomSession session, [Description("Optional substring to filter anchor names")] string? filter = null) {
      var model = session.Require();
      var names = model.Anchors
         .Where(a => string.IsNullOrEmpty(filter) || a.Contains(filter!, StringComparison.OrdinalIgnoreCase))
         .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
         .ToList();
      return JsonSerializer.Serialize(new { count = names.Count, tables = names }, Json);
   }

   [McpServerTool(Name = "read_table")]
   [Description("Read a named table as JSON rows. Provide the anchor name (e.g. 'data.pokemon.stats'). Use start/count to page through large tables.")]
   public string ReadTable(
      RomSession session,
      [Description("Anchor/table name, e.g. data.pokemon.stats")] string name,
      [Description("First row index to return")] int start = 0,
      [Description("Maximum rows to return")] int count = 25) {
      var model = session.Require();
      var table = model.GetTableModel(name);
      if (table == null) return JsonSerializer.Serialize(new { error = $"No table named '{name}'. Use list_tables to discover names." }, Json);

      var fields = table.Run.ElementContent
         .Where(s => !string.IsNullOrEmpty(s.Name))
         .Select(s => s.Name)
         .ToList();

      var rows = new List<Dictionary<string, object?>>();
      int end = Math.Min(table.Count, Math.Max(0, start) + Math.Max(0, count));
      for (int i = Math.Max(0, start); i < end; i++) {
         var element = table[i];
         var row = new Dictionary<string, object?> { ["index"] = i };
         foreach (var seg in table.Run.ElementContent) {
            if (string.IsNullOrEmpty(seg.Name)) continue;
            try {
               row[seg.Name] = seg.Type == ElementContentType.PCS
                  ? element.GetStringValue(seg.Name)
                  : element.GetValue(seg.Name);
            } catch {
               row[seg.Name] = null;
            }
         }
         rows.Add(row);
      }

      return JsonSerializer.Serialize(new {
         name,
         total = table.Count,
         start,
         returned = rows.Count,
         fields,
         rows,
      }, Json);
   }

   // ----------------------------------------------------------------------
   // STUBS — implement these in the Ralph loop. Each throws so that an
   // unimplemented tool is obvious to the caller rather than silently wrong.
   // Reference patterns are in docs/ and PROMPT.md.
   // ----------------------------------------------------------------------

   [McpServerTool(Name = "write_value")]
   [Description("Set a single integer field on a table row (does not save to disk by itself). NOT YET IMPLEMENTED.")]
   public string WriteValue(RomSession session, string table, int index, string field, int value) =>
      throw new NotImplementedException(
         "write_value: get model.GetTableModel(table); var token = new ModelDelta(); " +
         "element.SetValue(field, value, token). Then expose save via save_rom. See PROMPT.md.");

   [McpServerTool(Name = "export_table")]
   [Description("Export a full table to a JSON file on disk (encounters, trainers, dex, etc.). NOT YET IMPLEMENTED.")]
   public string ExportTable(RomSession session, string name, string outPath) =>
      throw new NotImplementedException(
         "export_table: read every row like read_table (no paging) and File.WriteAllText(outPath, json). See PROMPT.md.");

   [McpServerTool(Name = "run_script")]
   [Description("Parse/run an HMA script against the loaded ROM and return output. NOT YET IMPLEMENTED.")]
   public string RunScript(RomSession session, string script) =>
      throw new NotImplementedException(
         "run_script: use HavenSoft.HexManiac.Core.Models.Code.ScriptParser with session.Singletons.ScriptLines. See PROMPT.md.");

   [McpServerTool(Name = "save_rom")]
   [Description("Write the (possibly edited) ROM back to disk, optionally to a new path. NOT YET IMPLEMENTED.")]
   public string SaveRom(RomSession session, string? outPath = null) =>
      throw new NotImplementedException(
         "save_rom: File.WriteAllBytes(outPath ?? session.RomPath, model.RawData). See PROMPT.md.");
}
