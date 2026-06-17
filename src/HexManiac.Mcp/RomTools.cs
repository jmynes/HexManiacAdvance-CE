using System.ComponentModel;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels;
using ModelContextProtocol.Server;

namespace HavenSoft.HexManiac.Mcp;

// MCP tools for reading/editing GBA Pokémon ROM data via HexManiac.Core.
//
// The RomSession parameter is supplied by dependency injection (a registered
// singleton); the remaining parameters are the tool's arguments. Each tool
// returns a JSON string. Logical failures return { "error": ... } rather than
// throwing, so the client gets a readable message.
[McpServerToolType]
public sealed class RomTools {
   private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
   private static readonly JsonSerializerOptions JsonIndented = new() { WriteIndented = true };

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
      if (table == null) return NoTable(name);
      var rows = ReadRows(table, start, count);
      return JsonSerializer.Serialize(new {
         name,
         total = table.Count,
         start,
         returned = rows.Count,
         fields = FieldNames(table),
         rows,
      }, Json);
   }

   [McpServerTool(Name = "write_value")]
   [Description("Set a single integer field on a table row (in memory; call save_rom to persist). Returns old and new values.")]
   public string WriteValue(
      RomSession session,
      [Description("Anchor/table name")] string table,
      [Description("Row index")] int index,
      [Description("Field name within the row")] string field,
      [Description("New integer value")] int value) {
      var model = session.Require();
      // Use the ViewPort's real change token so the edit actually persists.
      var t = model.GetTableModel(table, () => session.Token);
      if (t == null) return NoTable(table);
      if (index < 0 || index >= t.Count) return Err($"index {index} out of range (0..{t.Count - 1}).");
      var row = t[index];
      if (!row.HasField(field)) return Err($"No field '{field}' on table '{table}'.");
      int oldValue = row.GetValue(field);
      row.SetValue(field, value);
      int newValue = t[index].GetValue(field);
      return JsonSerializer.Serialize(new { ok = true, table, index, field, oldValue, newValue }, Json);
   }

   [McpServerTool(Name = "export_table")]
   [Description("Export an entire table (all rows, no paging) to a JSON file on disk. Use for encounters, trainers, dex, etc.")]
   public string ExportTable(
      RomSession session,
      [Description("Anchor/table name to export")] string name,
      [Description("Absolute path of the .json file to write")] string outPath) {
      var model = session.Require();
      var table = model.GetTableModel(name);
      if (table == null) return NoTable(name);
      var rows = ReadRows(table, 0, table.Count);
      var json = JsonSerializer.Serialize(new {
         name,
         total = table.Count,
         fields = FieldNames(table),
         rows,
      }, JsonIndented);
      File.WriteAllText(outPath, json);
      return JsonSerializer.Serialize(new { ok = true, name, rows = rows.Count, path = outPath }, Json);
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
         if (!File.Exists(full)) return Err($"Script file not found: {full}");
         // TryImport sets the path context so relative includes in the script resolve.
         vp.TryImport(new LoadedFile(full, File.ReadAllBytes(full)), session.FileSystem);
      } else if (!string.IsNullOrEmpty(script)) {
         vp.Edit(script);
      } else {
         return Err("Provide 'script' text or 'path' to a .hma file.");
      }

      vp.ChangeHistory.ChangeCompleted();
      var errors = session.Errors.ToList();
      return JsonSerializer.Serialize(new {
         ok = errors.Count == 0,
         errors,
         messages = session.Messages.ToList(),
      }, Json);
   }

   [McpServerTool(Name = "save_rom")]
   [Description("Write the (possibly edited) ROM to disk. Defaults to the loaded path; pass outPath to write a copy.")]
   public string SaveRom(RomSession session, [Description("Optional output path; defaults to the loaded ROM path")] string? outPath = null) {
      var model = session.Require();
      var target = string.IsNullOrEmpty(outPath) ? session.RomPath : outPath;
      if (string.IsNullOrEmpty(target)) return Err("No output path and no loaded path to default to.");
      session.RequireViewPort().ChangeHistory.ChangeCompleted();
      File.WriteAllBytes(target, model.RawData);
      return JsonSerializer.Serialize(new { ok = true, path = target, length = model.RawData.Length }, Json);
   }

   // ---- helpers ----

   private static List<string> FieldNames(ModelTable table) =>
      table.Run.ElementContent.Where(s => !string.IsNullOrEmpty(s.Name)).Select(s => s.Name).ToList();

   private static List<Dictionary<string, object?>> ReadRows(ModelTable table, int start, int count) {
      var rows = new List<Dictionary<string, object?>>();
      int begin = Math.Max(0, start);
      int end = Math.Min(table.Count, begin + Math.Max(0, count));
      for (int i = begin; i < end; i++) {
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
      return rows;
   }

   private static string NoTable(string name) =>
      JsonSerializer.Serialize(new { error = $"No table named '{name}'. Use list_tables to discover names." }, Json);

   private static string Err(string message) =>
      JsonSerializer.Serialize(new { error = message }, Json);
}
