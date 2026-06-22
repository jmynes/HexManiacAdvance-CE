using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
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

      public static List<Dictionary<string, object?>> ReadRows(ModelTable table, int start, int count,
            ISet<int> skip = null, Action<int, Dictionary<string, object?>> annotate = null) {
         var rows = new List<Dictionary<string, object?>>();
         int begin = Math.Max(0, start);
         int end = Math.Min(table.Count, begin + Math.Max(0, count));
         for (int i = begin; i < end; i++) {
            if (skip != null && skip.Contains(i)) continue;
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
            annotate?.Invoke(i, row);
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

      // The script "specials" for the loaded ROM (resolved by name, so romhack-aware). A script calls
      // these as `special <index>` / `special2 <var> <index>`; the index is the position in this list.
      public static object ListSpecials(IDataModel model, string? filter) {
         var opts = model.GetOptions("specials");
         var specials = new List<object>();
         if (opts != null) {
            for (int i = 0; i < opts.Count; i++) {
               var name = opts[i];
               if (string.IsNullOrEmpty(name)) continue;
               if (!string.IsNullOrEmpty(filter) && !name.Contains(filter!, StringComparison.OrdinalIgnoreCase)) continue;
               specials.Add(new Dictionary<string, object?> { ["index"] = i, ["name"] = name });
            }
         }
         return new Dictionary<string, object?> {
            ["count"] = specials.Count, ["total"] = opts?.Count ?? 0, ["specials"] = specials,
            ["note"] = "Called from scripts as `special <index>`; names are resolved for the loaded ROM. Use the `reference` tool (kind=script) for command syntax.",
         };
      }

      public static object ReadTable(IDataModel model, string name, int start, int count, bool includePlaceholders = false) {
         var table = model.GetTableModel(name);
         if (table == null) return Err($"No table named '{name}'. Use list_tables to discover names.");
         bool species = IsSpeciesIndexed(name, table);
         var skip = (!includePlaceholders && species) ? PlaceholderSlots(model) : null;
         var rows = ReadRows(table, start, count, skip, species ? SpeciesAnnotator(model) : null);
         var fields = FieldNames(table);
         if (species) fields.Add("slug");
         var result = new Dictionary<string, object?> {
            ["name"] = name,
            ["total"] = table.Count,
            ["start"] = start,
            ["returned"] = rows.Count,
            ["fields"] = fields,
            ["rows"] = rows,
         };
         AddPlaceholderNote(result, skip);
         return result;
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
               static string NormFlag(string s) => s.Trim().Trim('"');
               var match = flags.FirstOrDefault(f => NormFlag(f).Equals(NormFlag(flag), StringComparison.OrdinalIgnoreCase));
               if (match == null) return Err($"Unknown flag '{flag}' on field '{field}'. Flags: {string.Join(", ", flags.Select(NormFlag))}");
               if (!TryCoerceFlag(value, out var flagVal)) return Err($"Flag '{flag}' expects true/false (or 0/1).");
               var oldFlag = ((ModelTupleElement)element[field])[match];
               ((ModelTupleElement)element[field])[match] = flagVal;
               var newFlag = ((ModelTupleElement)t[index][field])[match];
               return WriteResult(table, index, field, NormFlag(match), oldFlag, newFlag);
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
               if (!TryCoerceInt(value, out var iv)) return Err($"Field '{field}' is an integer; provide a number value.");
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

      // The MCP client serializes the value argument as a JSON string (untyped schema),
      // so a numeric/bool string must coerce to the field's type. Raw int/bool (from a
      // typed JSON client) still work.
      private static bool TryCoerceInt(object value, out int result) {
         switch (value) {
            case int i: result = i; return true;
            case string s when int.TryParse(s, out result): return true;
            default: result = 0; return false;
         }
      }

      private static bool TryCoerceFlag(object value, out int result) {
         switch (value) {
            case bool b: result = b ? 1 : 0; return true;
            case int i: result = i; return true;
            case string s when bool.TryParse(s, out var bs): result = bs ? 1 : 0; return true;
            case string s when int.TryParse(s, out result): return true;
            default: result = 0; return false;
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

      public static object RowRange(IDataModel model, string table, int index, int count) {
         var t = model.GetTableModel(table);
         if (t == null) return Err($"No table named '{table}'.");
         if (count < 1) return Err("count must be at least 1.");
         if (index < 0 || index + count > t.Count) return Err($"rows {index}..{index + count - 1} out of range (0..{t.Count - 1}).");
         var first = t[index];
         int length = 0;
         for (int i = 0; i < count; i++) length += t[index + i].Length;
         return new Dictionary<string, object?> {
            ["ok"] = true, ["table"] = table, ["index"] = index, ["count"] = count,
            ["start"] = first.Start, ["length"] = length,
         };
      }

      public static object CopyRows(IDataModel model, string table, int index, int count) {
         var range = RowRange(model, table, index, count);
         if (range is Dictionary<string, object?> e && e.ContainsKey("error")) return range;
         var r = (Dictionary<string, object?>)range;
         int start = (int)r["start"]!, length = (int)r["length"]!;
         var sb = new System.Text.StringBuilder(length * 2);
         for (int i = 0; i < length; i++) sb.Append(model.RawData[start + i].ToString("X2"));
         return new Dictionary<string, object?> {
            ["ok"] = true, ["table"] = table, ["index"] = index, ["count"] = count,
            ["bytes"] = sb.ToString(), ["elementLength"] = length / count,
         };
      }

      public static object PasteRows(IDataModel model, Func<ModelDelta> token, string table, int index, string hex) {
         if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return Err("paste data must be a non-empty hex string with an even length.");
         var bytes = new byte[hex.Length / 2];
         for (int i = 0; i < bytes.Length; i++) {
            if (!byte.TryParse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
               return Err($"paste data is not valid hex at byte {i}.");
         }
         var t = model.GetTableModel(table, token);
         if (t == null) return Err($"No table named '{table}'.");
         if (index < 0 || index >= t.Count) return Err($"index {index} out of range (0..{t.Count - 1}).");
         int elementLength = t[index].Length;
         if (bytes.Length % elementLength != 0) return Err($"paste size {bytes.Length} is not a multiple of the element length {elementLength}.");
         int count = bytes.Length / elementLength;
         if (index + count > t.Count) return Err($"pasting {count} rows at {index} exceeds the table (0..{t.Count - 1}).");
         token().ChangeData(model, t[index].Start, bytes);
         return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["index"] = index, ["count"] = count };
      }

      public static object ListShortcuts(IDataModel model) {
         var shortcuts = model.GotoShortcuts
            .Select(s => new Dictionary<string, object?> { ["display"] = s.DisplayText, ["anchor"] = s.GotoAnchor })
            .ToList();
         return new Dictionary<string, object?> { ["count"] = shortcuts.Count, ["shortcuts"] = shortcuts };
      }

      public static object ExportToFile(IDataModel model, string name, string outPath, bool includePlaceholders = false) {
         var table = model.GetTableModel(name);
         if (table == null) return Err($"No table named '{name}'.");
         bool species = IsSpeciesIndexed(name, table);
         var skip = (!includePlaceholders && species) ? PlaceholderSlots(model) : null;
         var rows = ReadRows(table, 0, table.Count, skip, species ? SpeciesAnnotator(model) : null);
         var fields = FieldNames(table);
         if (species) fields.Add("slug");
         var payload = new Dictionary<string, object?> {
            ["name"] = name, ["total"] = table.Count, ["fields"] = fields, ["rows"] = rows,
         };
         AddPlaceholderNote(payload, skip);
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         var result = new Dictionary<string, object?> { ["ok"] = true, ["name"] = name, ["rows"] = rows.Count, ["path"] = outPath };
         AddPlaceholderNote(result, skip);
         return result;
      }

      // ---- species-indexed table awareness ----
      // Many Gen-3 tables are indexed by species: the species name table itself, or
      // any table whose length is tied to it (stats, level-up moves, tm/tutor
      // compatibility, ...). For those we (a) drop the unused "limbo" placeholder
      // slots by default and (b) annotate each row with a canonical `slug` (+ form
      // info) so ROM names line up with external sources. Non-species tables are
      // left untouched.

      // True when 'name'/'table' is indexed by the species name table.
      private static bool IsSpeciesIndexed(string name, ModelTable table) =>
         name == HardcodeTablesModel.PokemonNameTable
         || (table.Run is ArrayRun ar && ar.LengthFromAnchor == HardcodeTablesModel.PokemonNameTable);

      // The species name table and its text field name, or (null, null).
      private static (ModelTable names, string field) SpeciesNameSource(IDataModel model) {
         var names = model.GetTableModel(HardcodeTablesModel.PokemonNameTable);
         var field = names?.Run.ElementContent
            .FirstOrDefault(s => s.Type == ElementContentType.PCS && !string.IsNullOrEmpty(s.Name))?.Name;
         return field == null ? (null, null) : (names, field);
      }

      // True when a species name is a placeholder (blank, or only '?' characters).
      public static bool IsPlaceholderSpeciesName(string name) =>
         string.IsNullOrWhiteSpace(name) || name.Trim().All(c => c == '?');

      // The set of placeholder species indices (from the species name table).
      private static ISet<int> PlaceholderSlots(IDataModel model) {
         var (names, field) = SpeciesNameSource(model);
         if (names == null) return null;
         var slots = new HashSet<int>();
         for (int i = 0; i < names.Count; i++) {
            string value = null;
            try { value = names[i].GetStringValue(field); } catch { }
            if (IsPlaceholderSpeciesName(value)) slots.Add(i);
         }
         return slots.Count > 0 ? slots : null;
      }

      // A per-row annotator that adds the canonical species `slug`, plus `forms`
      // (and Deoxys's game-specific `defaultForm`) for multi-form species.
      private static Action<int, Dictionary<string, object?>> SpeciesAnnotator(IDataModel model) {
         var (names, field) = SpeciesNameSource(model);
         if (names == null) return null;
         var deoxysForme = PokemonSpeciesNaming.DeoxysFormeFor(model.GetGameCode());
         return (i, row) => {
            if (i < 0 || i >= names.Count) return;
            string name = null;
            try { name = names[i].GetStringValue(field); } catch { }
            var slug = PokemonSpeciesNaming.Slug(name);
            if (slug == null) return;
            row["slug"] = slug;
            var forms = PokemonSpeciesNaming.FormsFor(slug);
            if (forms != null) {
               row["forms"] = forms;
               if (slug == "deoxys" && deoxysForme != null) row["defaultForm"] = deoxysForme;
            }
         };
      }

      private static void AddPlaceholderNote(Dictionary<string, object?> result, ISet<int> skip) {
         if (skip == null || skip.Count == 0) return;
         result["excludedPlaceholders"] = skip.Count;
         result["placeholderNote"] =
            $"Excluded {skip.Count} placeholder/limbo species slots (internal Unown-variant indices, not real species). Pass includePlaceholders=true to include them.";
      }

      // Decode the script at `address` to HMA's readable script text - the same listing the code
      // sidebar shows. `parser` is the engine (xse / battle / animation / ai), already chosen by the
      // caller; `type` is just echoed back. Length is measured with the same FindLength the GUI uses.
      public static object ReadScript(IDataModel model, Code.ScriptParser parser, string address, string type) {
         if (parser == null) return Err("No script parser available (open a ROM with the code tool).");
         if (!TryParseAddress(address, out int addr) || addr < 0 || addr >= model.Count)
            return Err($"Bad address '{address}'. Use a hex address like 0x16C474.");
         int length;
         try { length = parser.FindLength(model, addr); }
         catch (Exception e) { return Err($"Couldn't measure the script at {addr:X6}: {e.Message}"); }
         int sections = 0;
         string text;
         try { text = parser.Parse(model, addr, length, ref sections); }
         catch (Exception e) { return Err($"Couldn't decode the script at {addr:X6}: {e.Message}"); }
         return new Dictionary<string, object?> {
            ["ok"] = true, ["address"] = addr.ToString("X6"), ["type"] = type,
            ["length"] = length, ["script"] = text,
         };
      }

      // Accept a hex address with or without a 0x prefix (ROM offsets are hex); fall back to decimal.
      private static bool TryParseAddress(string s, out int addr) {
         addr = -1;
         if (string.IsNullOrWhiteSpace(s)) return false;
         s = s.Trim();
         if (s.StartsWith("0x") || s.StartsWith("0X")) s = s.Substring(2);
         return int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out addr)
             || int.TryParse(s, out addr);
      }

      public static Dictionary<string, object?> Err(string message) => new() { ["error"] = message };
   }
}
