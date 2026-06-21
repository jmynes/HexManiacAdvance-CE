using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Runs;

namespace HavenSoft.HexManiac.Core.Models {
   // Dumps every trainer and their team to JSON. Trainer-level fields come from the
   // trainers table; each party member is parsed from the `tpt` struct it points to
   // (layout varies by structType). For members WITHOUT hardcoded moves, the in-game
   // default moveset is filled in (the last <=4 level-up moves at or below the mon's
   // level) so the output matches what the game would actually generate.
   public static class TrainerTeamExport {
      private const int INCLUDE_MOVES = TrainerPokemonTeamRun.INCLUDE_MOVES; // structType bit 0
      private const int INCLUDE_ITEM = TrainerPokemonTeamRun.INCLUDE_ITEM;   // structType bit 1
      private const string ClassNamesTable = "data.trainers.classes.names";

      public static object Export(IDataModel model, string outPath, bool includeDefaultMoves = true) {
         var trainers = model.GetTableModel(HardcodeTablesModel.TrainerTableName);
         if (trainers == null) return RomAutomation.Err($"No table named '{HardcodeTablesModel.TrainerTableName}'.");

         var speciesNames = NameColumn(model, HardcodeTablesModel.PokemonNameTable);
         var moveNames = NameColumn(model, HardcodeTablesModel.MoveNamesTable);
         var itemNames = NameColumn(model, HardcodeTablesModel.ItemsTableName);
         var classNames = NameColumn(model, ClassNamesTable);
         int levelUpCount = model.GetTableModel(HardcodeTablesModel.LevelMovesTableName)?.Count ?? 0;

         var list = new List<Dictionary<string, object?>>();
         int totalMons = 0;
         for (int t = 0; t < trainers.Count; t++) {
            var el = trainers[t];
            int structType = GetInt(el, "structType");
            int ptr = GetInt(el, "pokemon");
            int count = GetInt(el, "pokemonCount");
            var party = ParseParty(model, ptr, count, structType, includeDefaultMoves, levelUpCount,
               speciesNames, moveNames, itemNames);
            totalMons += party.Count;
            var battleItems = new[] { "item1", "item2", "item3", "item4" }
               .Select(f => GetInt(el, f)).Where(i => i != 0).Select(i => Name(itemNames, i)).ToList();
            list.Add(new Dictionary<string, object?> {
               ["index"] = t,
               ["name"] = ReadableText(GetStr(el, "name")),
               ["class"] = Name(classNames, GetInt(el, "class")),
               ["classId"] = GetInt(el, "class"),
               ["doubleBattle"] = GetInt(el, "doubleBattle") != 0,
               ["aiFlags"] = GetInt(el, "ai"),
               ["battleItems"] = battleItems,
               ["structType"] = structType,
               ["customMoves"] = (structType & INCLUDE_MOVES) != 0,
               ["heldItems"] = (structType & INCLUDE_ITEM) != 0,
               ["partyCount"] = count,
               ["party"] = party,
            });
         }

         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP — data.trainers.stats + tpt party structs",
            ["trainerCount"] = list.Count,
            ["totalPartyPokemon"] = totalMons,
            ["notes"] = new Dictionary<string, object?> {
               ["hardcodedMoves"] = "Per party member: true when the trainer stores explicit moves for it (structType bit 0). When false, 'moves' is the game's default level-up moveset for that species/level (only when includeDefaultMoves).",
               ["moves"] = "Hardcoded movesets keep exactly what is stored (may be fewer than 4). Default movesets are the last <=4 level-up moves at or below the mon's level.",
               ["ivSpread"] = "Raw 0-255 value stored in the ROM; 'iv' is that scaled to 0-31 and applied to every stat.",
               ["structType"] = "0=no item/default moves, 1=custom moves, 2=held item, 3=held item+custom moves.",
            },
            ["trainers"] = list,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
         return new Dictionary<string, object?> {
            ["ok"] = true, ["trainerCount"] = list.Count, ["totalPartyPokemon"] = totalMons,
            ["defaultMovesFilled"] = includeDefaultMoves, ["path"] = outPath,
         };
      }

      private static List<Dictionary<string, object?>> ParseParty(IDataModel model, int ptr, int count,
            int structType, bool includeDefaultMoves, int levelUpCount,
            List<string> speciesNames, List<string> moveNames, List<string> itemNames) {
         var party = new List<Dictionary<string, object?>>();
         if (ptr <= 0 || count <= 0) return party;
         int stride = (structType & INCLUDE_MOVES) != 0 ? 16 : 8;
         for (int m = 0; m < count; m++) {
            int basis = ptr + m * stride;
            if (basis < 0 || basis + stride > model.Count) break;
            int ivSpread = model.ReadMultiByteValue(basis + TrainerPokemonTeamRun.PokemonFormat_FixedIVStart, 2);
            int level = model.ReadMultiByteValue(basis + TrainerPokemonTeamRun.PokemonFormat_LevelStart, 2);
            int species = model.ReadMultiByteValue(basis + TrainerPokemonTeamRun.PokemonFormat_PokemonStart, 2);
            bool hardcoded = (structType & INCLUDE_MOVES) != 0;
            var member = new Dictionary<string, object?> {
               ["level"] = level,
               ["species"] = Name(speciesNames, species),
               ["speciesId"] = species,
               ["ivSpread"] = ivSpread,
               ["iv"] = (int)Math.Round(ivSpread * TrainerPokemonTeamRun.IV_Cap / 255.0),
               ["hardcodedMoves"] = hardcoded,
            };
            int moveBase = TrainerPokemonTeamRun.PokemonFormat_MoveStart; // 6
            if ((structType & INCLUDE_ITEM) != 0) {
               int held = model.ReadMultiByteValue(basis + TrainerPokemonTeamRun.PokemonFormat_ItemStart, 2);
               moveBase = TrainerPokemonTeamRun.PokemonFormat_ItemStart + 2; // 8
               if (held != 0) {
                  member["heldItem"] = Name(itemNames, held);
                  member["heldItemId"] = held;
               }
            }
            if (hardcoded) {
               var ids = Enumerable.Range(0, 4).Select(j => model.ReadMultiByteValue(basis + moveBase + j * 2, 2));
               member["moves"] = ids.Where(id => id != 0).Select(id => Name(moveNames, id)).ToList();
            } else if (includeDefaultMoves && species >= 0 && species < levelUpCount) {
               var ids = TrainerPokemonTeamRun.GetDefaultMoves(model, species, level);
               member["moves"] = ids.Where(id => id != 0).Select(id => Name(moveNames, id)).ToList();
            }
            party.Add(member);
         }
         return party;
      }

      // Render HMA's text-glyph escapes to readable labels (PK/MN glyphs, gender symbols).
      public static string ReadableText(string s) {
         if (string.IsNullOrEmpty(s)) return s;
         return s.Replace("\\sf", "♀").Replace("\\sm", "♂")
                 .Replace("\\pk\\mn", "PKMN").Replace("\\pk", "PK").Replace("\\mn", "MN").Trim();
      }

      private static List<string> NameColumn(IDataModel model, string tableName) {
         var list = new List<string>();
         var table = model.GetTableModel(tableName);
         if (table == null) return list;
         var field = table.Run.ElementContent
            .FirstOrDefault(s => s.Type == ElementContentType.PCS && !string.IsNullOrEmpty(s.Name))?.Name;
         for (int i = 0; i < table.Count; i++) {
            string v = null;
            if (field != null) { try { v = table[i].GetStringValue(field); } catch { } }
            list.Add(ReadableText(v));
         }
         return list;
      }

      private static string Name(List<string> names, int id) =>
         id >= 0 && id < names.Count && !string.IsNullOrEmpty(names[id]) ? names[id] : $"#{id}";

      private static int GetInt(ModelArrayElement el, string field, int dflt = 0) {
         try { return el.HasField(field) ? el.GetValue(field) : dflt; } catch { return dflt; }
      }
      private static string GetStr(ModelArrayElement el, string field) {
         try { return el.HasField(field) ? el.GetStringValue(field) : ""; } catch { return ""; }
      }
   }
}
