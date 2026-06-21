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
      private const byte GivePokemon = 0x79;
      private const byte SetWildBattle = 0xB6;
      private const byte Special = 0x25;
      // The 'StartLegendaryBattle' special (FRLG special 0x138, named in the engine): the
      // Navel Rock / Birth Island ticket legendaries set VAR_0x8004 (species) + VAR_0x8005 (level)
      // right before it, instead of using setwildbattle. (The birds/Mewtwo also call it, but get
      // their species from a setwildbattle, so they have no nearby VAR_0x8004 and are skipped here.)
      private const int StartLegendaryBattle = 0x138;
      private const int Setvar = 0x16, Var_0x8004 = 0x8004, Var_0x8005 = 0x8005, Var_0x8006 = 0x8006;

      public static object Export(IDataModel model, ScriptParser parser, string outPath) {
         if (parser == null) return RomAutomation.Err("export_script_encounters needs the script parser (open a ROM with the code tool / live GUI).");
         var speciesNames = TrainerTeamExport.NameColumn(model, HardcodeTablesModel.PokemonNameTable);
         var itemNames = TrainerTeamExport.NameColumn(model, HardcodeTablesModel.ItemsTableName);

         var sites = CollectSites(model, parser, speciesNames, itemNames);
         int gift = sites.Count(s => (string)s["kind"] == "gift");
         int stat = sites.Count(s => (string)s["kind"] == "static");
         int legend = sites.Count(s => (string)s["kind"] == "legendary");

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
            ["source"] = "HexManiacAdvance MCP — map-script walk for givePokemon (0x79) + setwildbattle (0xB6) + StartLegendaryBattle special",
            ["notes"] = new Dictionary<string, object?> {
               ["kind"] = "gift = givePokemon (0x79), handed to the player (fossils, Eevee, the Magikarp sale, ...). static = setwildbattle (0xB6), a scripted/standing battle (legendary birds, Mewtwo, Snorlax, ...). legendary = the StartLegendaryBattle special with species/level loaded into VAR_0x8004/0x8005 just before it - the Navel Rock / Birth Island ticket legendaries (Ho-Oh, Lugia, Deoxys).",
               ["scriptOffset"] = "Bare uppercase hex address of the command.",
               ["level"] = "The level baked into the script (gifts/statics/legendaries use a fixed level).",
               ["heldItem"] = "Item argument, null when 0.",
               ["coverage"] = "Only commands reachable from top-level map scripts (object events + map-header scripts) are walked; hand-written ASM is not covered. A givePokemon whose species is loaded into a variable first (the Dojo Hitmons, Game Corner) reads as 0 and is skipped; the InitRoamer beasts pick their species in ASM and aren't here either.",
            },
            ["giftCount"] = gift,
            ["staticCount"] = stat,
            ["legendaryCount"] = legend,
            ["siteCount"] = sites.Count,
            ["speciesCovered"] = bySpecies.Count,
            ["bySpecies"] = bySpeciesOut,
            ["sites"] = sites,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> {
            ["ok"] = true, ["giftCount"] = gift, ["staticCount"] = stat, ["legendaryCount"] = legend,
            ["siteCount"] = sites.Count, ["speciesCovered"] = bySpecies.Count, ["path"] = outPath,
         };
      }

      // Scan backward from `addr` for a `setvar <var>, value` (opcode 0x16) and return its value,
      // or -1. Used to recover the species/level a ticket-legendary loads into VAR_0x8004/0x8005
      // right before StartLegendaryBattle. Small window so it only matches the adjacent setup.
      private static int BackScanSetvar(IDataModel model, int addr, int var, int maxBack = 48) {
         for (int k = 3; k <= maxBack; k++) {
            int a = addr - k;
            if (a < 0) break;
            if (model[a] == Setvar && model.ReadMultiByteValue(a + 1, 2) == var) return model.ReadMultiByteValue(a + 3, 2);
         }
         return -1;
      }

      private static List<Dictionary<string, object?>> CollectSites(IDataModel model, ScriptParser parser,
            List<string> speciesNames, List<string> itemNames) {
         var sites = new List<Dictionary<string, object?>>();
         var mapNames = TrainerTeamExport.MapNameColumn(model);

         void Record(int scriptStart, int bank, int mapNumber, string mapName) {
            if (scriptStart < 0 || scriptStart >= model.Count) return;
            IEnumerable<ScriptSpot> spots;
            try { spots = Flags.GetAllScriptSpots(model, parser, new[] { scriptStart }, GivePokemon, SetWildBattle, Special).ToList(); }
            catch { return; }
            foreach (var spot in spots) {
               try {
                  byte op = model[spot.Address];
                  int species, level, item;
                  string kind;
                  if (op == GivePokemon || op == SetWildBattle) {
                     species = model.ReadMultiByteValue(spot.Address + 1, 2); // species:u16 @ +1, level:u8 @ +3, item:u16 @ +4
                     level = model[spot.Address + 3];
                     item = model.ReadMultiByteValue(spot.Address + 4, 2);
                     kind = op == GivePokemon ? "gift" : "static";
                  } else if (op == Special && model.ReadMultiByteValue(spot.Address + 1, 2) == StartLegendaryBattle) {
                     // ticket legendary: species/level are loaded into VAR_0x8004/0x8005 just before the special
                     species = BackScanSetvar(model, spot.Address, Var_0x8004);
                     if (species < 0) continue;                 // birds/Mewtwo path: species came from a setwildbattle instead
                     level = Math.Max(0, BackScanSetvar(model, spot.Address, Var_0x8005));
                     item = Math.Max(0, BackScanSetvar(model, spot.Address, Var_0x8006));
                     kind = "legendary";
                  } else continue;
                  if (species <= 0 || species >= speciesNames.Count) continue; // SPECIES_NONE / var-loaded / stray parse
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
