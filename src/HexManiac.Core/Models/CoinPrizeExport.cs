using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using HavenSoft.HexManiac.Core.Models.Runs;

namespace HavenSoft.HexManiac.Core.Models {
   // Reads coin-prize Pokemon (the Celadon Game Corner) straight from the ROM instead of a curated
   // list. The prize menu is a `scripts.text.multichoice` entry whose option text spells out
   // "<SPECIES> <n> COINS" (e.g. "PORYGON 9,999 COINS") - the cost is the menu text, not a numeric
   // field, which is why a plain table scan can't find it. We walk every multichoice option, keep
   // the ones that both name a real species and end in COINS, and read the cost from the text. An
   // edited ROM (different prizes/costs) reports its own values with no curation.
   public static class CoinPrizeExport {
      private const string MultichoiceTable = "scripts.text.multichoice";

      public static object Export(IDataModel model, string outPath) {
         var table = model.GetTableModel(MultichoiceTable);
         if (table == null) return RomAutomation.Err($"No table named '{MultichoiceTable}'.");
         var speciesNames = TrainerTeamExport.NameColumn(model, HardcodeTablesModel.PokemonNameTable);
         // normalized species name -> id, longest first so e.g. PORYGON2 is tried before PORYGON
         var byName = speciesNames
            .Select((n, id) => (norm: Norm(n), id))
            .Where(x => x.norm.Length > 0)
            .OrderByDescending(x => x.norm.Length)
            .ToList();

         var prizes = new List<Dictionary<string, object?>>();
         var seen = new HashSet<int>();
         int start = table.Run.Start, stride = table.Run.ElementLength, count = table.Run.ElementCount;
         for (int i = 0; i < count; i++) {
            int entry = start + i * stride;
            int optionsPtr, optCount;
            try { optionsPtr = model.ReadPointer(entry); optCount = model.ReadMultiByteValue(entry + 4, 4); }
            catch { continue; }
            if (optionsPtr < 0 || optionsPtr >= model.Count || optCount <= 0 || optCount > 64) continue;
            for (int j = 0; j < optCount; j++) {
               try {
                  string text = TrainerTeamExport.ReadPcsString(model, model.ReadPointer(optionsPtr + j * 8));
                  if (string.IsNullOrEmpty(text)) continue;
                  var cost = Regex.Match(text, @"([\d,]+)\s*COINS", RegexOptions.IgnoreCase);
                  if (!cost.Success || !int.TryParse(cost.Groups[1].Value.Replace(",", ""), out int coins)) continue;
                  var norm = Norm(text);
                  var hit = byName.FirstOrDefault(x => norm.StartsWith(x.norm, StringComparison.Ordinal));
                  if (hit.norm == null || !seen.Add(hit.id)) continue;
                  prizes.Add(new Dictionary<string, object?> {
                     ["species"] = speciesNames[hit.id],
                     ["speciesId"] = hit.id,
                     ["coins"] = coins,
                     ["multichoiceIndex"] = i,
                     ["optionText"] = text,
                  });
               } catch { }
            }
         }

         var payload = new Dictionary<string, object?> {
            ["source"] = $"HexManiacAdvance MCP - {MultichoiceTable} option text matching '<species> <n> COINS'",
            ["note"] = "Coin-prize Pokemon read live from the in-game prize menu (the Celadon Game Corner). Species + cost come from the menu option text, so an edited ROM reports its own prizes. The give itself is in a menu jump-table script the standard walk can't reach, so it's the only Pokemon-obtain method that lives in this menu.",
            ["prizeCount"] = prizes.Count,
            ["prizes"] = prizes,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> { ["ok"] = true, ["prizeCount"] = prizes.Count, ["path"] = outPath };
      }

      // Keep only A-Z0-9 (uppercased) so species prefixes match regardless of spaces, gender
      // glyphs, or rendered control codes sitting between the name and the cost in the menu text.
      private static string Norm(string s) {
         if (string.IsNullOrEmpty(s)) return "";
         var sb = new StringBuilder();
         foreach (var c in s.ToUpperInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
         return sb.ToString();
      }
   }
}
