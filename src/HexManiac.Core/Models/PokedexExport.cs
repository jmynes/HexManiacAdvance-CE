using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Runs;

namespace HavenSoft.HexManiac.Core.Models {
   // Reads the Pokedex flavor table (data.pokedex.stats) - category, height, weight, and the dex
   // entry text - straight from the ROM. The table is indexed by NATIONAL dex number; height is
   // stored in decimeters and weight in hectograms (both converted to m / kg here). The category
   // field holds just the prefix ("SEED"), to which the games append " POKEMON".
   public static class PokedexExport {
      private const string DexTable = "data.pokedex.stats";

      public static object Export(IDataModel model, string outPath) {
         var table = model.GetTableModel(DexTable);
         if (table == null) return RomAutomation.Err($"No table named '{DexTable}'.");
         var byDex = new Dictionary<string, object?>();
         for (int dex = 1; dex < table.Count; dex++) {  // entry 0 is a dummy (MissingNo-style)
            try {
               var el = table[dex];
               string category = Clean(GetStr(el, "species"));   // 12-char category prefix, e.g. "SEED"
               int height = GetInt(el, "height");                // decimeters
               int weight = GetInt(el, "weight");                // hectograms
               string entry = Clean(GetStr(el, "description1"));
               if (string.IsNullOrEmpty(category) && height == 0 && weight == 0 && string.IsNullOrEmpty(entry)) continue;
               byDex[dex.ToString()] = new Dictionary<string, object?> {
                  ["category"] = string.IsNullOrEmpty(category) ? null : category + " Pokémon",
                  ["heightM"] = Math.Round(height / 10.0, 1),
                  ["weightKg"] = Math.Round(weight / 10.0, 1),
                  ["dexEntry"] = entry,
               };
            } catch { }
         }
         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP — data.pokedex.stats",
            ["note"] = "Keyed by NATIONAL dex number. Category is the prefix the game shows before 'POKEMON'. Height/weight are converted from the ROM's decimeters/hectograms to metres/kilograms.",
            ["count"] = byDex.Count,
            ["byNationalDex"] = byDex,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> { ["ok"] = true, ["count"] = byDex.Count, ["path"] = outPath };
      }

      // Dex entry text breaks lines/pages with real CR/LF (and sometimes \n \p \l escapes);
      // flatten all of them to single spaces so the entry is one clean sentence.
      private static string Clean(string s) {
         if (string.IsNullOrEmpty(s)) return s;
         s = TrainerTeamExport.ReadableText(s).Trim('"');
         s = s.Replace("\r", " ").Replace("\n", " ")
              .Replace("\\n", " ").Replace("\\p", " ").Replace("\\l", " ").Replace("\\r", " ");
         while (s.Contains("  ")) s = s.Replace("  ", " ");
         return s.Trim();
      }

      private static string GetStr(ModelArrayElement el, string field) {
         try { return el.HasField(field) ? el.GetStringValue(field) : ""; } catch { return ""; }
      }
      private static int GetInt(ModelArrayElement el, string field) {
         try { return el.HasField(field) ? el.GetValue(field) : 0; } catch { return 0; }
      }
   }
}
