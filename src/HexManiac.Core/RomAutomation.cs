using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Runs;

namespace HavenSoft.HexManiac.Core.Models {
   // Pipe envelope shared by the GUI automation server and the MCP client.
   public record AutoRequest(string Method, JsonElement Params);
   public record AutoResponse(bool Ok, JsonElement? Result, string? Error);

   // Single implementation of the ROM read/write/export logic, over an
   // IDataModel. Used by both the headless MCP server and the in-GUI automation
   // server so the behavior (and JSON shape) is identical in both modes.
   public static class RomAutomation {
      public static List<string> FieldNames(ModelTable table) =>
         table.Run.ElementContent.Where(s => !string.IsNullOrEmpty(s.Name)).Select(s => s.Name).ToList();

      public static List<Dictionary<string, object?>> ReadRows(ModelTable table, int start, int count) {
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

      public static object ListTables(IDataModel model, string? filter) {
         var names = model.Anchors
            .Where(a => string.IsNullOrEmpty(filter) || a.Contains(filter!, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();
         return new Dictionary<string, object?> { ["count"] = names.Count, ["tables"] = names };
      }

      public static object ReadTable(IDataModel model, string name, int start, int count) {
         var table = model.GetTableModel(name);
         if (table == null) return Err($"No table named '{name}'. Use list_tables to discover names.");
         var rows = ReadRows(table, start, count);
         return new Dictionary<string, object?> {
            ["name"] = name,
            ["total"] = table.Count,
            ["start"] = start,
            ["returned"] = rows.Count,
            ["fields"] = FieldNames(table),
            ["rows"] = rows,
         };
      }

      public static object WriteValue(IDataModel model, Func<ModelDelta> token, string table, int index, string field, int value) {
         var t = model.GetTableModel(table, token);
         if (t == null) return Err($"No table named '{table}'.");
         if (index < 0 || index >= t.Count) return Err($"index {index} out of range (0..{t.Count - 1}).");
         var row = t[index];
         if (!row.HasField(field)) return Err($"No field '{field}' on table '{table}'.");
         int oldValue = row.GetValue(field);
         row.SetValue(field, value);
         int newValue = t[index].GetValue(field);
         return new Dictionary<string, object?> {
            ["ok"] = true, ["table"] = table, ["index"] = index, ["field"] = field,
            ["oldValue"] = oldValue, ["newValue"] = newValue,
         };
      }

      public static object ExportToFile(IDataModel model, string name, string outPath) {
         var table = model.GetTableModel(name);
         if (table == null) return Err($"No table named '{name}'.");
         var rows = ReadRows(table, 0, table.Count);
         var payload = new Dictionary<string, object?> {
            ["name"] = name, ["total"] = table.Count, ["fields"] = FieldNames(table), ["rows"] = rows,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
         return new Dictionary<string, object?> { ["ok"] = true, ["name"] = name, ["rows"] = rows.Count, ["path"] = outPath };
      }

      public static Dictionary<string, object?> Err(string message) => new() { ["error"] = message };
   }
}
