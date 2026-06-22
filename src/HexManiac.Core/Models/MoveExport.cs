using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Runs;

namespace HavenSoft.HexManiac.Core.Models {
   // Dumps every move to JSON: stats from data.pokemon.moves.stats.battle, the RESOLVED description
   // text, the effect NAME from HMA's own `moveeffectoptions` enum (the dropdown labels like
   // "SleepPrimary"), type name, decoded target/flags, and TM/HM/tutor membership. Also emits the full
   // effectId -> name map so consumers have the effect legend in one place.
   public static class MoveExport {
      private const string Stats = "data.pokemon.moves.stats.battle";
      private const string Names = "data.pokemon.moves.names";
      private const string Descriptions = "data.pokemon.moves.descriptions";
      private const string TmList = "data.pokemon.moves.tms";       // FRLG: 50 TMs then 8 HMs
      private const string TutorList = "data.pokemon.moves.tutors"; // the move each move-tutor teaches

      public static object Export(IDataModel model, string outPath) {
         var stats = model.GetTableModel(Stats);
         if (stats == null) return RomAutomation.Err($"export_moves needs the move table ({Stats}) - open a base game or a ROM with move metadata.");
         var names = model.GetTableModel(Names);
         var descs = model.GetTableModel(Descriptions);

         var effectNames = model.GetOptions("moveeffectoptions");
         var typeNames = model.GetOptions("data.pokemon.type.names");
         var targetOpts = model.GetOptions("movetarget");
         var infoOpts = model.GetOptions("moveinfo");

         // move id -> "TM##"/"HM##" and -> tutor index
         var tmOf = new Dictionary<int, string>();
         var tms = model.GetTableModel(TmList);
         if (tms != null) for (int i = 0; i < tms.Count; i++) {
            int mv = GetInt(tms[i], "move");
            if (mv > 0 && !tmOf.ContainsKey(mv)) tmOf[mv] = i < 50 ? $"TM{i + 1:00}" : $"HM{i - 49:00}";
         }
         var tutorOf = new Dictionary<int, int>();
         var tutors = model.GetTableModel(TutorList);
         if (tutors != null) for (int i = 0; i < tutors.Count; i++) {
            int mv = GetInt(tutors[i], "move");
            if (mv > 0 && !tutorOf.ContainsKey(mv)) tutorOf[mv] = i;
         }

         var moves = new List<Dictionary<string, object?>>();
         for (int i = 0; i < stats.Count; i++) {
            var el = stats[i];
            int effect = GetInt(el, "effect"), type = GetInt(el, "type");
            // the descriptions table is keyed from move 1 (no entry for move 0 / NONE), so move i is at descs[i-1].
            string desc = null;
            if (descs != null && i >= 1 && i - 1 < descs.Count) try { desc = TrainerTeamExport.ReadPcsString(model, descs[i - 1].GetAddress("description")); } catch { }
            moves.Add(new Dictionary<string, object?> {
               ["id"] = i,
               ["name"] = i < (names?.Count ?? 0) ? TrainerTeamExport.ReadableText(GetStr(names[i], "name")) : null,
               ["effectId"] = effect,
               ["effect"] = Lookup(effectNames, effect),
               ["power"] = GetInt(el, "power"),
               ["typeId"] = type,
               ["type"] = Lookup(typeNames, type),
               ["accuracy"] = GetInt(el, "accuracy"),
               ["pp"] = GetInt(el, "pp"),
               ["effectChance"] = GetInt(el, "effectAccuracy"),
               ["priority"] = GetInt(el, "priority"),
               ["target"] = DecodeFlags(GetInt(el, "target"), targetOpts),
               ["flags"] = DecodeFlags(GetInt(el, "info"), infoOpts),
               ["description"] = desc,
               ["tm"] = tmOf.TryGetValue(i, out var tm) ? tm : null,
               ["isTutorMove"] = tutorOf.ContainsKey(i),
               ["tutorIndex"] = tutorOf.TryGetValue(i, out var ti) ? (object)ti : null,
            });
         }

         var effectMap = new Dictionary<string, object?>();
         if (effectNames != null) for (int i = 0; i < effectNames.Count; i++) if (!string.IsNullOrEmpty(effectNames[i])) effectMap[i.ToString()] = effectNames[i];

         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP - data.pokemon.moves.stats.battle + names/descriptions, effect names from HMA's moveeffectoptions enum, TM/HM/tutor membership joined.",
            ["moveCount"] = moves.Count,
            ["note"] = "effect is HMA's dropdown label (e.g. SleepPrimary); see moveEffects for the full id->name legend. target/flags are decoded from their bitfields. tm is TM##/HM## or null; TMs/HMs are items - use export_item_locations for where they're sold/found.",
            ["moveEffects"] = effectMap,
            ["moves"] = moves,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> { ["ok"] = true, ["moveCount"] = moves.Count, ["effectsNamed"] = effectMap.Count, ["path"] = outPath };
      }

      // a |b[] bitfield byte -> the set option names (or the raw int if the option list is missing).
      private static object DecodeFlags(int raw, IReadOnlyList<string> opts) {
         if (opts == null || opts.Count == 0) return raw;
         var set = new List<string>();
         for (int b = 0; b < opts.Count && b < 32; b++) if ((raw & (1 << b)) != 0 && !string.IsNullOrEmpty(opts[b])) set.Add(opts[b]);
         return set;
      }
      private static string Lookup(IReadOnlyList<string> opts, int i) => opts != null && i >= 0 && i < opts.Count ? opts[i] : null;
      private static int GetInt(ModelArrayElement el, string field, int dflt = 0) {
         try { return el.HasField(field) ? el.GetValue(field) : dflt; } catch { return dflt; }
      }
      private static string GetStr(ModelArrayElement el, string field) {
         try { return el.HasField(field) ? el.GetStringValue(field) : ""; } catch { return ""; }
      }
   }
}
