using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Map;

namespace HavenSoft.HexManiac.Core.Models {
   // Walks every top-level map script for the move-tutor specials (SelectMoveTutorMon /
   // ChooseMonForMoveTutor / DisplayMoveTutorMenu - resolved BY NAME, so it works across games/romhacks)
   // and reports WHERE each move-tutor NPC is, plus the move it teaches when that's set into VAR_0x8005/
   // 0x8004 just before the special. NOTE: vanilla FRLG has very few real tutors (mainly the Cape Brink
   // ultimate-starter-move tutor, whose move is chosen by your starter); Emerald's Battle Frontier tutors
   // are where this pays off. data.pokemon.moves.tutors is the table of candidate tutor moves.
   public static class MoveTutorLocationExport {
      private const int Var_8004 = 0x8004, Var_8005 = 0x8005;
      private static readonly HashSet<string> TutorSpecials = new() { "SelectMoveTutorMon", "ChooseMonForMoveTutor", "DisplayMoveTutorMenu" };

      public static object Export(IDataModel model, ScriptParser parser, string outPath) {
         if (parser == null) return RomAutomation.Err("export_move_tutors needs the script parser (open a ROM with the code tool / live GUI).");
         byte setvar = parser.CommandCode("setvar") ?? 0;
         byte special = parser.CommandCode("special") ?? 0;
         if (setvar == 0 || special == 0) return RomAutomation.Err("This engine doesn't define setvar/special - can't walk for move tutors.");

         var specialNames = new Dictionary<int, string>();
         var opts = model.GetOptions("specials");
         if (opts != null) for (int i = 0; i < opts.Count; i++) if (!string.IsNullOrEmpty(opts[i]) && TutorSpecials.Contains(opts[i])) specialNames[i] = opts[i];

         var moveNames = model.GetOptions("data.pokemon.moves.names");
         int moveCount = moveNames?.Count ?? 0;
         var mapNames = TrainerTeamExport.MapNameColumn(model);
         var filter = new[] { setvar, special };
         var sites = new List<Dictionary<string, object?>>();
         var seen = new HashSet<string>();

         void Record(int scriptStart, int bank, int mapNumber, string mapName) {
            if (scriptStart < 0 || scriptStart >= model.Count) return;
            IEnumerable<ScriptSpot> spots;
            try { spots = Flags.GetAllScriptSpots(model, parser, new[] { scriptStart }, filter).ToList(); }
            catch { return; }
            foreach (var spot in spots) {
               try {
                  if (model[spot.Address] != special) continue;
                  int idx = model.ReadMultiByteValue(spot.Address + 1, 2);
                  if (!specialNames.TryGetValue(idx, out var specialName)) continue;
                  // the taught move is loaded into VAR_0x8005 (or 0x8004) just before the special
                  int move = BackScanSetvar(model, setvar, spot.Address, Var_8005);
                  if (move <= 0 || move >= moveCount) move = BackScanSetvar(model, setvar, spot.Address, Var_8004);
                  string moveName = move > 0 && move < moveCount && !string.IsNullOrEmpty(moveNames[move]) ? moveNames[move] : null;
                  if (!seen.Add($"{idx}:{spot.Address}")) continue;
                  sites.Add(new Dictionary<string, object?> {
                     ["special"] = specialName,
                     ["move"] = moveName,
                     ["moveId"] = moveName != null ? (object)move : null,
                     ["mapBank"] = bank,
                     ["mapNumber"] = mapNumber,
                     ["mapName"] = mapName,
                     ["scriptOffset"] = TrainerTeamExport.FormatOffset(spot.Address),
                  });
               } catch { }
            }
         }
         TrainerTeamExport.WalkTopLevelMapScripts(model, mapNames, Record);

         // the candidate tutor moves the ROM lists (data.pokemon.moves.tutors); many are vestigial in FRLG.
         var candidates = new List<object>();
         var tutors = model.GetTableModel("data.pokemon.moves.tutors");
         if (tutors != null) for (int i = 0; i < tutors.Count; i++) {
            int mv = 0; try { mv = tutors[i].GetValue("move"); } catch { }
            candidates.Add(new Dictionary<string, object?> { ["tutorIndex"] = i, ["moveId"] = mv, ["move"] = mv > 0 && mv < moveCount ? moveNames[mv] : null });
         }

         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP - top-level map-script walk for the move-tutor specials (resolved by name), with the taught move back-scanned from VAR_0x8005/0x8004.",
            ["note"] = "A site is a move-tutor NPC location. 'move' is null when the tutor picks the move in script logic (e.g. the Cape Brink tutor chooses by your starter) rather than a single setvar. candidateTutorMoves is data.pokemon.moves.tutors - vanilla FRLG only locates a couple of these; Emerald's Battle Frontier tutors are denser.",
            ["tutorSiteCount"] = sites.Count,
            ["sites"] = sites,
            ["candidateTutorMoves"] = candidates,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> { ["ok"] = true, ["tutorSiteCount"] = sites.Count, ["candidateTutorMoves"] = candidates.Count, ["path"] = outPath };
      }

      // Scan backward from `addr` for `setvar <var>, value` and return its value, or -1.
      private static int BackScanSetvar(IDataModel model, byte setvarOp, int addr, int var, int maxBack = 64) {
         for (int k = 3; k <= maxBack; k++) {
            int a = addr - k;
            if (a < 0) break;
            if (model[a] == setvarOp && model.ReadMultiByteValue(a + 1, 2) == var) return model.ReadMultiByteValue(a + 3, 2);
         }
         return -1;
      }
   }
}
