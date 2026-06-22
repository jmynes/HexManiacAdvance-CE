using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Runs;

namespace HavenSoft.HexManiac.Core.Models {
   // Dumps the item table (data.items.stats) to JSON with the description text RESOLVED.
   // read_table / export_table return the `description` field as a raw text-pointer int; HMA's
   // sidebar renders it, and so does this - via the same PCS decode the trainer/pokedex exports
   // use (TrainerTeamExport.ReadPcsString). Price/pocket/hold-effect come straight off the table.
   public static class ItemExport {
      public static object Export(IDataModel model, string outPath) {
         var table = model.GetTableModel(HardcodeTablesModel.ItemsTableName);
         if (table == null) return RomAutomation.Err("export_items needs the item table (data.items.stats) - open a base game or a ROM with item metadata.");

         var items = new List<Dictionary<string, object?>>();
         for (int i = 0; i < table.Count; i++) {
            var el = table[i];
            string description = null;
            try { description = TrainerTeamExport.ReadPcsString(model, el.GetAddress("description")); } catch { }
            items.Add(new Dictionary<string, object?> {
               ["id"] = i,
               ["name"] = TrainerTeamExport.ReadableText(GetStr(el, "name")),
               ["price"] = GetInt(el, "price"),
               ["description"] = description,
               // pocket is a plain int with no name table in vanilla, and its numbering differs
               // between FRLG and RSE - so we report the raw id rather than guess a cross-game name.
               ["pocketId"] = GetInt(el, "pocket"),
               ["holdEffect"] = GetInt(el, "holdeffect"),
               ["holdEffectParam"] = GetInt(el, "param"),
               ["type"] = GetInt(el, "type"),
            });
         }

         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP - data.items.stats with the description text-pointer resolved (the same PCS decode HMA's sidebar shows).",
            ["itemCount"] = items.Count,
            ["items"] = items,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> { ["ok"] = true, ["itemCount"] = items.Count, ["path"] = outPath };
      }

      private static int GetInt(ModelArrayElement el, string field, int dflt = 0) {
         try { return el.HasField(field) ? el.GetValue(field) : dflt; } catch { return dflt; }
      }
      private static string GetStr(ModelArrayElement el, string field) {
         try { return el.HasField(field) ? el.GetStringValue(field) : ""; } catch { return ""; }
      }
   }
}
