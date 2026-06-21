using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.ViewModels.Map;

namespace HavenSoft.HexManiac.Core.Models {
   // Walks every top-level map script and collects the species-granting script commands that the
   // wild-encounter / trainer / evolution tables don't cover: givePokemon (0x79 = gifts: starters,
   // fossils, Eevee, the Magikarp salesman, ...) and setwildbattle (0xB6 = scripted/static battles:
   // the legendary birds, Mewtwo, Snorlax, ...). For each site it records species/level/held-item
   // plus the map bank/number/name and the script offset, so callers can answer "where do I get
   // species X by script" the same way export_trainers' "uses" answers it for trainers.
   //
   // Both commands share the same leading arg layout: species:u16 @ +1, level:u8 @ +3, item:u16 @ +4.
   public static class EncounterScriptExport {
      // Variable space + the special script vars that carry a species/level into a give. These are
      // stable Gen-3 engine conventions, NOT per-game offsets - unlike the command opcodes (givePokemon,
      // setwildbattle, setvar, special) and the special indices (StartLegendaryBattle, InitRoamer),
      // which are resolved BY NAME at runtime in CollectSites so this keeps working on other games
      // and romhacks that use a different opcode map or specials table.
      private const int VariableBase = 0x4000;  // script vars are >= 0x4000; real species ids are below that
      private const int Var_Species = 0x8004, Var_Level = 0x8005, Var_Item = 0x8006, Var_RoamerSpecies = 0x8000;
      private const int Var_TradeIndex = 0x8008;  // in-game trade NPCs put the trade id in VAR_0x8008

      public static object Export(IDataModel model, ScriptParser parser, string outPath) {
         if (parser == null) return RomAutomation.Err("export_script_encounters needs the script parser (open a ROM with the code tool / live GUI).");
         var speciesNames = TrainerTeamExport.NameColumn(model, HardcodeTablesModel.PokemonNameTable);
         var itemNames = TrainerTeamExport.NameColumn(model, HardcodeTablesModel.ItemsTableName);

         var tradeLocations = new SortedDictionary<int, Dictionary<string, object?>>();
         var sites = CollectSites(model, parser, speciesNames, itemNames, tradeLocations);
         int gift = sites.Count(s => (string)s["kind"] == "gift");
         int stat = sites.Count(s => (string)s["kind"] == "static");
         int legend = sites.Count(s => (string)s["kind"] == "legendary");
         int roam = sites.Count(s => (string)s["kind"] == "roaming");

         // group by species id so callers can join straight onto a species table
         var bySpecies = new SortedDictionary<int, List<Dictionary<string, object?>>>();
         foreach (var s in sites) {
            int sp = (int)s["speciesId"]!;
            if (!bySpecies.TryGetValue(sp, out var bucket)) bySpecies[sp] = bucket = new();
            bucket.Add(s);
         }
         var bySpeciesOut = new Dictionary<string, object?>();
         foreach (var kv in bySpecies) bySpeciesOut[kv.Key.ToString()] = kv.Value;

         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP — map-script walk for givePokemon + setwildbattle + the StartLegendaryBattle/InitRoamer specials (commands/specials resolved by name, so it works across games/romhacks)",
            ["notes"] = new Dictionary<string, object?> {
               ["kind"] = "gift = givePokemon, handed to the player (starters, fossils, Eevee, the Magikarp sale, the Fighting-Dojo Hitmons, ...). static = setwildbattle, a scripted/standing battle (birds, Mewtwo, Snorlax, ...). legendary = the StartLegendaryBattle special with species/level in VAR_0x8004/0x8005 (the Navel Rock / Birth Island ticket legendaries). roaming = the InitRoamer special with the species in a var (CFRU-style hacks; vanilla picks it in ASM and is absent).",
               ["scriptOffset"] = "Bare uppercase hex address of the command.",
               ["level"] = "The level baked into the script (gifts/statics/legendaries use a fixed level).",
               ["heldItem"] = "Item argument, null when 0.",
               ["resolution"] = "Command opcodes (givePokemon/setwildbattle/setvar/special) and special indices (StartLegendaryBattle/InitRoamer) are resolved BY NAME from the engine + GetOptions('specials'), not hardcoded - so a romhack with a different opcode map or specials table still works.",
               ["coverage"] = "Only commands reachable from top-level map scripts (object events + map-header scripts) are walked; hand-written ASM is not covered. A 'givePokemon VAR' is resolved from the setvar that fed it when that var was set exactly once in the walk (the Dojo Hitmons, the starters). A var set many times before the give is a runtime menu (the Game Corner prizes) - left to export_coin_prizes. Vanilla InitRoamer picks its species in ASM (no setvar) so the roaming beasts are absent there.",
            },
            ["giftCount"] = gift,
            ["staticCount"] = stat,
            ["legendaryCount"] = legend,
            ["roamingCount"] = roam,
            ["siteCount"] = sites.Count,
            ["speciesCovered"] = bySpecies.Count,
            ["bySpecies"] = bySpeciesOut,
            ["sites"] = sites,
            ["tradeLocations"] = tradeLocations.ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value),
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> {
            ["ok"] = true, ["giftCount"] = gift, ["staticCount"] = stat, ["legendaryCount"] = legend, ["roamingCount"] = roam,
            ["siteCount"] = sites.Count, ["speciesCovered"] = bySpecies.Count, ["path"] = outPath,
         };
      }

      // Scan backward from `addr` for a `setvar <var>, value` and return its value, or -1. Used to
      // recover the species/level a legendary/roamer loads into a var right before its special.
      private static int BackScanSetvar(IDataModel model, byte setvarOp, int addr, int var, int maxBack = 48) {
         for (int k = 3; k <= maxBack; k++) {
            int a = addr - k;
            if (a < 0) break;
            if (model[a] == setvarOp && model.ReadMultiByteValue(a + 1, 2) == var) return model.ReadMultiByteValue(a + 3, 2);
         }
         return -1;
      }

      // True if `special <specialIndex>` appears within `maxFwd` bytes after `addr` (a small forward
      // peek used to spot the battle starter that follows a setwildbattle).
      private static bool HasSpecialWithin(IDataModel model, byte specialOp, int specialIndex, int addr, int maxFwd) {
         if (specialIndex < 0) return false;
         for (int k = 0; k <= maxFwd; k++) {
            int a = addr + k;
            if (a < 0 || a + 3 > model.Count) break;
            if (model[a] == specialOp && model.ReadMultiByteValue(a + 1, 2) == specialIndex) return true;
         }
         return false;
      }

      // Index of a named special (e.g. "StartLegendaryBattle") in this game's specials table, or -1.
      private static int SpecialIndex(IDataModel model, string name) {
         try {
            var opts = model.GetOptions("specials");
            if (opts != null) for (int i = 0; i < opts.Count; i++) if (opts[i] == name) return i;
         } catch { }
         return -1;
      }

      private static List<Dictionary<string, object?>> CollectSites(IDataModel model, ScriptParser parser,
            List<string> speciesNames, List<string> itemNames, SortedDictionary<int, Dictionary<string, object?>> tradeLocations) {
         var sites = new List<Dictionary<string, object?>>();
         var seen = new HashSet<string>();  // dedupe a site reached from more than one event
         var mapNames = TrainerTeamExport.MapNameColumn(model);

         // Resolve commands + specials by name for THIS game (romhack-safe).
         byte givePokemon = parser.CommandCode("givePokemon") ?? 0;
         byte setWildBattle = parser.CommandCode("setwildbattle") ?? 0;
         byte setvar = parser.CommandCode("setvar") ?? 0;
         byte special = parser.CommandCode("special") ?? 0;
         if (givePokemon == 0 || setvar == 0) return sites;  // engine doesn't define the basics we rely on
         int startLegendaryBattle = SpecialIndex(model, "StartLegendaryBattle");
         int initRoamer = SpecialIndex(model, "InitRoamer");
         int createInGameTrade = SpecialIndex(model, "CreateInGameTradePokemon");
         int startMarowakBattle = SpecialIndex(model, "StartMarowakBattle");  // the uncatchable ghost battle
         var filter = new List<byte> { givePokemon, setvar };
         if (setWildBattle != 0) filter.Add(setWildBattle);
         if (special != 0) filter.Add(special);
         var filterArray = filter.ToArray();

         void Record(int scriptStart, int bank, int mapNumber, string mapName) {
            if (scriptStart < 0 || scriptStart >= model.Count) return;
            IEnumerable<ScriptSpot> spots;
            try { spots = Flags.GetAllScriptSpots(model, parser, new[] { scriptStart }, filterArray).ToList(); }
            catch { return; }
            var varValues = new Dictionary<int, int>();   // var -> last literal value set in this walk
            var varSetCount = new Dictionary<int, int>();  // how many times each var was set (>1 = ambiguous)
            foreach (var spot in spots) {
               try {
                  byte op = model[spot.Address];
                  if (op == setvar) {
                     int v = model.ReadMultiByteValue(spot.Address + 1, 2);
                     varValues[v] = model.ReadMultiByteValue(spot.Address + 3, 2);
                     varSetCount[v] = (varSetCount.TryGetValue(v, out var c) ? c : 0) + 1;
                     continue;
                  }
                  int species, level, item;
                  string kind;
                  if (op == givePokemon || op == setWildBattle) {
                     species = model.ReadMultiByteValue(spot.Address + 1, 2); // species:u16 @ +1, level:u8 @ +3, item:u16 @ +4
                     if (species >= VariableBase) {
                        // variable species: resolve only when that var was set exactly once in this walk.
                        // A var set many times before the give is a runtime jump-table (e.g. the Game
                        // Corner prize menu) we can't pin down - leave those to export_coin_prizes.
                        if (!varValues.TryGetValue(species, out var resolved)) continue;
                        if (!varSetCount.TryGetValue(species, out var cnt) || cnt != 1) continue;
                        species = resolved;
                     }
                     level = model[spot.Address + 3];
                     item = model.ReadMultiByteValue(spot.Address + 4, 2);
                     // a setwildbattle immediately handed to StartMarowakBattle is the uncatchable Pokemon
                     // Tower ghost (a forced plot battle), not an obtainable encounter - skip it.
                     if (op == setWildBattle && startMarowakBattle >= 0 && HasSpecialWithin(model, special, startMarowakBattle, spot.Address, 16)) continue;
                     kind = op == givePokemon ? "gift" : "static";
                  } else if (op == special) {
                     int idx = model.ReadMultiByteValue(spot.Address + 1, 2);
                     if (createInGameTrade >= 0 && idx == createInGameTrade) {
                        // in-game trade NPC: the trade id is the literal in VAR_0x8008, set once near the script top.
                        if (varValues.TryGetValue(Var_TradeIndex, out var ti) && varSetCount.TryGetValue(Var_TradeIndex, out var tc)
                            && tc == 1 && ti >= 0 && ti < 256 && !tradeLocations.ContainsKey(ti)) {
                           tradeLocations[ti] = new Dictionary<string, object?> {
                              ["tradeIndex"] = ti, ["mapBank"] = bank, ["mapNumber"] = mapNumber, ["mapName"] = mapName,
                              ["scriptOffset"] = TrainerTeamExport.FormatOffset(spot.Address),
                           };
                        }
                        continue;
                     }
                     if (startLegendaryBattle >= 0 && idx == startLegendaryBattle) {
                        // ticket legendary: species/level are loaded into VAR_0x8004/0x8005 just before the special
                        species = BackScanSetvar(model, setvar, spot.Address, Var_Species);
                        if (species < 0) continue;              // birds/Mewtwo path: species came from a setwildbattle instead
                        level = Math.Max(0, BackScanSetvar(model, setvar, spot.Address, Var_Level));
                        item = Math.Max(0, BackScanSetvar(model, setvar, spot.Address, Var_Item));
                        kind = "legendary";
                     } else if (initRoamer >= 0 && idx == initRoamer) {
                        // roamer: CFRU-style engines load the species into a var before InitRoamer; vanilla
                        // FRLG picks it in ASM (no setvar) and is skipped here, staying curated.
                        species = BackScanSetvar(model, setvar, spot.Address, Var_RoamerSpecies);
                        if (species < 0) species = BackScanSetvar(model, setvar, spot.Address, Var_Species);
                        if (species < 0) continue;
                        level = Math.Max(0, BackScanSetvar(model, setvar, spot.Address, Var_Level));
                        item = 0;
                        kind = "roaming";
                     } else continue;
                  } else continue;
                  if (species <= 0 || species >= speciesNames.Count) continue; // SPECIES_NONE / var-loaded / stray parse
                  if (!seen.Add($"{kind}:{species}:{spot.Address}")) continue; // already recorded from another event
                  sites.Add(new Dictionary<string, object?> {
                     ["kind"] = kind,
                     ["speciesId"] = species,
                     ["species"] = Name(speciesNames, species),
                     ["level"] = level,
                     ["heldItem"] = item != 0 ? Name(itemNames, item) : null,
                     ["mapBank"] = bank,
                     ["mapNumber"] = mapNumber,
                     ["mapName"] = mapName,
                     ["scriptOffset"] = TrainerTeamExport.FormatOffset(spot.Address),
                  });
               } catch { }
            }
         }

         TrainerTeamExport.WalkTopLevelMapScripts(model, mapNames, Record);
         return sites;
      }

      private static string Name(List<string> names, int id) =>
         id >= 0 && id < names.Count && !string.IsNullOrEmpty(names[id]) ? names[id] : $"#{id}";
   }
}
