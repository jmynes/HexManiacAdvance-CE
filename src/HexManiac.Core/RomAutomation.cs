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

      public static object WriteValue(IDataModel model, Func<ModelDelta> token,
            string table, int index, string field, object value, string flag = null) {
         var t = model.GetTableModel(table, token);
         if (t == null) return Err($"No table named '{table}'.");
         if (index < 0 || index >= t.Count) return Err($"index {index} out of range (0..{t.Count - 1}).");
         var element = t[index];
         if (!element.HasField(field)) return Err($"No field '{field}' on table '{table}'.");
         var seg = element.Table.ElementContent.First(s => s.Name == field);
         try {
            if (flag != null) {
               var flags = FlagNames(model, seg);
               if (flags == null) return Err($"Field '{field}' is not a bit-array; 'flag' only applies to bit-array fields.");
               if (!flags.Contains(flag)) return Err($"Unknown flag '{flag}' on field '{field}'. Flags: {string.Join(", ", flags)}");
               if (value is not bool && value is not int) return Err($"Flag '{flag}' expects true/false (or 0/1).");
               var oldFlag = ((ModelTupleElement)element[field])[flag];
               ((ModelTupleElement)element[field])[flag] = value;
               var newFlag = ((ModelTupleElement)t[index][field])[flag];
               return WriteResult(table, index, field, flag, oldFlag, newFlag);
            }
            if (seg is ArrayRunEnumSegment enumSeg) {
               if (value is string es) {
                  if (!enumSeg.TryParse(model, es, out _))
                     return Err($"Unknown value '{es}' for enum field '{field}'. Options: {string.Join(", ", enumSeg.GetOptions(model))}");
                  var old = element[field];
                  element[field] = es;
                  return WriteResult(table, index, field, null, old, t[index][field]);
               }
               if (value is int ei) {
                  var old = element[field];
                  element[field] = ei;
                  return WriteResult(table, index, field, null, old, t[index][field]);
               }
               return Err($"Field '{field}' is an enum; provide a name (string) or index (number).");
            }
            if (seg.Type == ElementContentType.PCS || seg.Type == ElementContentType.Pointer) {
               if (value is not string ps) return Err($"Field '{field}' is text; provide a string value.");
               var old = element.GetStringValue(field);
               element[field] = ps;
               return WriteResult(table, index, field, null, old, t[index].GetStringValue(field));
            }
            if (seg.Type == ElementContentType.Integer) {
               if (value is not int iv) return Err($"Field '{field}' is an integer; provide a number value.");
               var old = element.GetValue(field);
               element[field] = iv;
               return WriteResult(table, index, field, null, old, t[index].GetValue(field));
            }
            if (seg is ArrayRunBitArraySegment || seg is ArrayRunTupleSegment)
               return Err($"Field '{field}' is a bit-array; specify 'flag' to set a checkbox.");
            return Err($"Field '{field}' has a type that cannot be written.");
         } catch (Exception ex) {
            return Err($"Failed to set '{field}': {ex.Message}");
         }
      }

      private static IReadOnlyList<string> FlagNames(IDataModel model, ArrayRunElementSegment seg) =>
         seg is ArrayRunBitArraySegment b ? b.GetOptions(model)
         : seg is ArrayRunTupleSegment tp ? tp.Elements.Where(e => !string.IsNullOrEmpty(e.Name)).Select(e => e.Name).ToList()
         : null;

      private static Dictionary<string, object?> WriteResult(string table, int index, string field, string flag, object oldV, object newV) {
         var d = new Dictionary<string, object?> {
            ["ok"] = true, ["table"] = table, ["index"] = index, ["field"] = field,
            ["oldValue"] = oldV, ["newValue"] = newV,
         };
         if (flag != null) d["flag"] = flag;
         return d;
      }

      public static object ListShortcuts(IDataModel model) {
         var shortcuts = model.GotoShortcuts
            .Select(s => new Dictionary<string, object?> { ["display"] = s.DisplayText, ["anchor"] = s.GotoAnchor })
            .ToList();
         return new Dictionary<string, object?> { ["count"] = shortcuts.Count, ["shortcuts"] = shortcuts };
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
