using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Map;

namespace HavenSoft.HexManiac.Core.Models {
   // Mirrors HMA's "Show Uses" for species, headlessly and for every species at once: a cross-
   // reference of every place a `data.pokemon.names`-typed value points at each species, in the
   // two buckets the table tool uses -
   //   * tableRefs  : array fields typed data.pokemon.names (model.Arrays, the same set Show Uses
   //                  scans). This is where obtain sources that AREN'T give-commands live - the
   //                  three STARTERS (scripts.newgame.starters.*), in-game trades, evolutions,
   //                  battle-tower prizes, etc.
   //   * scriptRefs : every map-script COMMAND with a species arg (the generalization of
   //                  export_script_encounters from 2 hardcoded opcodes to all species-typed
   //                  commands, via ScriptParser.DependsOn), with map context.
   // Stream uses (a species' own level-up/egg movesets, trainer teams) are intentionally omitted:
   // they describe the species' data or where it is fought, not how it is obtained.
   public static class SpeciesSourceExport {
      private static readonly string SpeciesTable = HardcodeTablesModel.PokemonNameTable; // data.pokemon.names

      public static object Export(IDataModel model, ScriptParser parser, string outPath) {
         if (parser == null) return RomAutomation.Err("export_species_sources needs the script parser (open a ROM with the code tool / live GUI).");
         var speciesNames = TrainerTeamExport.NameColumn(model, SpeciesTable);
         int speciesCount = speciesNames.Count;

         var tableRefs = new SortedDictionary<int, List<Dictionary<string, object?>>>();
         var scriptRefs = new SortedDictionary<int, List<Dictionary<string, object?>>>();
         void AddTable(int sp, Dictionary<string, object?> r) { if (!tableRefs.TryGetValue(sp, out var l)) tableRefs[sp] = l = new(); l.Add(r); }
         void AddScript(int sp, Dictionary<string, object?> r) { if (!scriptRefs.TryGetValue(sp, out var l)) scriptRefs[sp] = l = new(); l.Add(r); }

         // --- table refs: scan each species-typed array field once, bucket rows by their value ---
         foreach (var table in model.Arrays) {
            string anchor = null;
            int segOffset = 0;
            foreach (var seg in table.ElementContent) {
               if (seg is ArrayRunEnumSegment en && en.EnumName == SpeciesTable) {
                  anchor ??= model.GetAnchorFromAddress(-1, table.Start);
                  for (int i = 0; i < table.ElementCount; i++) {
                     int addr = table.Start + i * table.ElementLength + segOffset;
                     if (addr < 0 || addr + seg.Length > model.Count) break;
                     int val = model.ReadMultiByteValue(addr, seg.Length);
                     if (val > 0 && val < speciesCount)
                        AddTable(val, new Dictionary<string, object?> { ["anchor"] = anchor, ["field"] = seg.Name, ["index"] = i });
                  }
               }
               segOffset += seg.Length;
            }
         }

         // --- script refs: every command that takes a species arg, with map context ---
         var filter = SpeciesCommandFilter(parser);
         if (filter.Count > 0) {
            var mapNames = TrainerTeamExport.MapNameColumn(model);
            void Record(int scriptStart, int bank, int mapNumber, string mapName) {
               if (scriptStart < 0 || scriptStart >= model.Count) return;
               IEnumerable<ScriptSpot> spots;
               try { spots = Flags.GetAllScriptSpots(model, parser, new[] { scriptStart }, filter.ToArray()).ToList(); }
               catch { return; }
               foreach (var spot in spots) {
                  try {
                     int check = spot.Address + spot.Line.LineCode.Count;
                     int? species = null;
                     foreach (var arg in spot.Line.Args) {
                        int length = arg.Length(model, check);
                        if (species == null && arg.EnumTableName == SpeciesTable) species = model.ReadMultiByteValue(check, length);
                        check += length;
                     }
                     if (species is int sp && sp > 0 && sp < speciesCount) {
                        AddScript(sp, new Dictionary<string, object?> {
                           ["command"] = spot.Line.LineCommand,
                           ["mapBank"] = bank, ["mapNumber"] = mapNumber, ["mapName"] = mapName,
                           ["scriptOffset"] = TrainerTeamExport.FormatOffset(spot.Address),
                        });
                     }
                  } catch { }
               }
            }
            TrainerTeamExport.WalkTopLevelMapScripts(model, mapNames, Record);
         }

         var bySpecies = new Dictionary<string, object?>();
         foreach (var sp in tableRefs.Keys.Union(scriptRefs.Keys).OrderBy(x => x)) {
            bySpecies[sp.ToString()] = new Dictionary<string, object?> {
               ["species"] = sp < speciesNames.Count ? speciesNames[sp] : $"#{sp}",
               ["tableRefs"] = tableRefs.TryGetValue(sp, out var t) ? t : new List<Dictionary<string, object?>>(),
               ["scriptRefs"] = scriptRefs.TryGetValue(sp, out var s) ? s : new List<Dictionary<string, object?>>(),
            };
         }
         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP — species cross-reference (HMA 'Show Uses': data.pokemon.names-typed table fields + script command args)",
            ["notes"] = new Dictionary<string, object?> {
               ["tableRefs"] = "Array fields typed data.pokemon.names equal to this species: anchor + field + element index. Carries obtain sources that aren't give-commands - scripts.newgame.starters.* (the 3 starters), in-game trades, evolutions, battle-tower prizes - alongside non-obtain refs.",
               ["scriptRefs"] = "Every map-script command with a species arg pointing at this species (generalizes givePokemon/setwildbattle to all species-typed commands), with command name + map + offset. Literal args only; a species loaded into a variable first (e.g. some Game Corner prizes) is not resolved.",
               ["omitted"] = "Stream uses - the species' own level-up/egg movesets and trainer teams - are not included (not obtain methods).",
            },
            ["speciesCovered"] = bySpecies.Count,
            ["bySpecies"] = bySpecies,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> {
            ["ok"] = true, ["speciesCovered"] = bySpecies.Count,
            ["tableRefSpecies"] = tableRefs.Count, ["scriptRefSpecies"] = scriptRefs.Count, ["path"] = outPath,
         };
      }

      // Opcodes of every script command that has a data.pokemon.names-typed arg (same construction
      // as TableTool.FindXseScriptUses' filter), so the walk visits exactly those commands.
      private static List<byte> SpeciesCommandFilter(ScriptParser parser) {
         var filter = new List<byte>();
         foreach (var line in parser.DependsOn(SpeciesTable)) {
            if (line is MacroScriptLine macro && macro.Args.Count > 0 && macro.Args[0] is SilentMatchArg silent) filter.Add(silent.ExpectedValue);
            else if (line is ScriptLine sl && sl.LineCode.Count > 0) filter.Add(sl.LineCode[0]);
         }
         return filter;
      }
   }
}
