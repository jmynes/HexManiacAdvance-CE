using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Map;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Map;

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

      public static object Export(IDataModel model, ScriptParser parser, string outPath, bool includeDefaultMoves = true, bool includeUses = true) {
         var trainers = model.GetTableModel(HardcodeTablesModel.TrainerTableName);
         if (trainers == null) return RomAutomation.Err($"No table named '{HardcodeTablesModel.TrainerTableName}'.");

         var speciesNames = NameColumn(model, HardcodeTablesModel.PokemonNameTable);
         var moveNames = NameColumn(model, HardcodeTablesModel.MoveNamesTable);
         var itemNames = NameColumn(model, HardcodeTablesModel.ItemsTableName);
         var classNames = NameColumn(model, ClassNamesTable);
         int levelUpCount = model.GetTableModel(HardcodeTablesModel.LevelMovesTableName)?.Count ?? 0;

         // Per-trainer script-reference sites (0x5C trainerbattle commands). Only walked
         // when includeUses is true and a parser is available; otherwise stays empty so
         // every trainer just gets an empty `uses` array (or none, when includeUses is off).
         var uses = includeUses && parser != null ? CollectTrainerUses(model, parser, trainers.Count) : null;
         int totalUses = uses?.Values.Sum(v => v.Count) ?? 0;

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
            var entry = new Dictionary<string, object?> {
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
            };
            if (uses != null) entry["uses"] = uses.TryGetValue(t, out var u) ? u : new List<Dictionary<string, object?>>();
            list.Add(entry);
         }

         var notes = new Dictionary<string, object?> {
            ["hardcodedMoves"] = "Per party member: true when the trainer stores explicit moves for it (structType bit 0). When false, 'moves' is the game's default level-up moveset for that species/level (only when includeDefaultMoves).",
            ["moves"] = "Hardcoded movesets keep exactly what is stored (may be fewer than 4). Default movesets are the last <=4 level-up moves at or below the mon's level.",
            ["ivSpread"] = "Raw 0-255 value stored in the ROM; 'iv' is that scaled to 0-31 and applied to every stat.",
            ["structType"] = "0=no item/default moves, 1=custom moves, 2=held item, 3=held item+custom moves.",
         };
         if (uses != null) notes["uses"] = "Where this trainer is referenced. source=\"script\": a map-script trainerbattle (opcode 0x5C) — scriptOffset (6-digit hex address), subtype (raw + name), map bank/number/name, and intro/win/lose dialogue (null when the subtype has none). source=\"rematch\": an entry in the rematch / VS-Seeker table — rematchIndex, the rematchSlots it fills (match1..match6), and the rematch map bank/number/name. An empty array means the trainer is referenced by neither (unused/placeholder/RSE-leftover). Hand-written ASM references are still not covered.";

         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP — data.trainers.stats + tpt party structs",
            ["trainerCount"] = list.Count,
            ["totalPartyPokemon"] = totalMons,
            ["notes"] = notes,
            ["trainers"] = list,
         };
         // Relaxed encoder so apostrophes, <>, and non-ASCII (accents, gender symbols) are
         // written literally in this data file rather than as \uXXXX escapes.
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         var result = new Dictionary<string, object?> {
            ["ok"] = true, ["trainerCount"] = list.Count, ["totalPartyPokemon"] = totalMons,
            ["defaultMovesFilled"] = includeDefaultMoves, ["path"] = outPath,
         };
         if (uses != null) { result["usesIncluded"] = true; result["totalUses"] = totalUses; }
         else result["usesIncluded"] = false;
         return result;
      }

      // The subtype byte (address+1) of a trainerbattle command -> HMA's name for it.
      // Names match scriptReference.txt; numbers without a stable cross-game name are left to the caller.
      private static readonly Dictionary<int, string> TrainerBattleSubtypes = new() {
         [0x00] = "single.battle",
         [0x01] = "single.battle.continue.silent",
         [0x02] = "single.battle.continue.music",
         [0x03] = "single.battle.nointro",
         [0x04] = "double.battle",
         [0x05] = "single.battle.rematch",
         [0x06] = "double.battle.continue.music",
         [0x07] = "double.battle.rematch",
         [0x08] = "double.battle.continue.silent",
         [0x09] = "single.battle.canlose", // FRLG canlose / Emerald pyramid; varies by game
      };

      // Walk every top-level map script (object events + map-header scripts), collect each
      // 0x5C trainerbattle site, and group them by trainer ID. Robust: a missing maps table,
      // null events, or a bad text pointer never aborts the whole export.
      private static Dictionary<int, List<Dictionary<string, object?>>> CollectTrainerUses(IDataModel model, ScriptParser parser, int trainerCount) {
         var byTrainer = new Dictionary<int, List<Dictionary<string, object?>>>();
         var mapNames = MapNameColumn(model);
         var mapNameByLocation = new Dictionary<(int bank, int map), string>();

         void Record(int scriptStart, int bank, int mapNumber, string mapName) {
            if (scriptStart < 0 || scriptStart >= model.Count) return;
            IEnumerable<ScriptSpot> spots;
            try { spots = Flags.GetAllScriptSpots(model, parser, new[] { scriptStart }, 0x5C).ToList(); }
            catch { return; }
            foreach (var spot in spots) {
               try {
                  int trainerId = model.ReadMultiByteValue(spot.Address + 2, 2);
                  if (trainerId <= 0 || trainerId >= trainerCount) continue; // skip TRAINER_NONE / stray parses
                  int subtype = model[spot.Address + 1];
                  var use = new Dictionary<string, object?> {
                     ["source"] = "script",
                     ["scriptOffset"] = FormatOffset(spot.Address),
                     ["subtype"] = subtype,
                     ["subtypeName"] = TrainerBattleSubtypes.TryGetValue(subtype, out var name) ? name : null,
                     ["mapBank"] = bank,
                     ["mapNumber"] = mapNumber,
                     ["mapName"] = mapName,
                     ["introText"] = null,
                     ["winText"] = null,
                     ["loseText"] = null,
                  };
                  ReadTextArgs(model, spot, use);
                  if (!byTrainer.TryGetValue(trainerId, out var bucket)) byTrainer[trainerId] = bucket = new();
                  bucket.Add(use);
               } catch { }
            }
         }

         AllMapsModel banks;
         try { banks = AllMapsModel.Create(model, default); } catch { return byTrainer; }
         for (int bankIndex = 0; bankIndex < banks.Count; bankIndex++) {
            MapBankModel? bank;
            try { bank = banks[bankIndex]; } catch { continue; }
            if (bank == null) continue;
            for (int mapIndex = 0; mapIndex < bank.Count; mapIndex++) {
               MapModel? map;
               try { map = bank[mapIndex]; } catch { continue; }
               if (map == null) continue;
               string mapName = null;
               try { int ni = map.NameIndex; if (ni >= 0 && ni < mapNames.Count) mapName = mapNames[ni]; } catch { }
               mapNameByLocation[(bankIndex, mapIndex)] = mapName;

               // object events
               try {
                  foreach (var obj in map.Events.Objects) {
                     if (obj == null) continue;
                     Record(obj.ScriptAddress, bankIndex, mapIndex, mapName);
                  }
               } catch { }

               // map-header scripts (same traversal as Flags.GetAllTopLevelScripts)
               try {
                  var headerScripts = map.MapScripts;
                  if (headerScripts != null) {
                     foreach (var script in headerScripts) {
                        if (script == null) continue;
                        if (script.GetValue("type").IsAny(2, 4)) {
                           var start = script.GetAddress("pointer");
                           if (start < 0 || start >= model.Count) continue;
                           int childCount = 0;
                           while (!model.ReadMultiByteValue(start, 2).IsAny(0, 0xFFFF)) {
                              Record(model.ReadPointer(start + 4), bankIndex, mapIndex, mapName);
                              start += 8;
                              if (++childCount > 100) break;
                           }
                        } else {
                           Record(script.GetAddress("pointer"), bankIndex, mapIndex, mapName);
                        }
                     }
                  }
               } catch { }
            }
         }

         // rematch / VS-Seeker table: trainers reachable only as rematch opponents have
         // no map-script trainerbattle, so fold the table in or they'd read as unused.
         // Each row is a rematch chain (match1..match6) with the rematch location.
         try {
            var rematch = model.GetTableModel(HardcodeTablesModel.RematchTable);
            if (rematch != null) {
               var slotNames = new[] { "match1", "match2", "match3", "match4", "match5", "match6" };
               for (int r = 0; r < rematch.Count; r++) {
                  var row = rematch[r];
                  int bank = GetInt(row, "mapbank"), mapNumber = GetInt(row, "map");
                  string mapName = mapNameByLocation.TryGetValue((bank, mapNumber), out var n) ? n : null;
                  // collect which rematch slots each trainer occupies in this row (dedupes padding)
                  var slotsByTrainer = new Dictionary<int, List<string>>();
                  foreach (var slot in slotNames) {
                     int tid = GetInt(row, slot);
                     if (tid <= 0 || tid >= trainerCount) continue;
                     if (!slotsByTrainer.TryGetValue(tid, out var ls)) slotsByTrainer[tid] = ls = new();
                     ls.Add(slot);
                  }
                  foreach (var kv in slotsByTrainer) {
                     var use = new Dictionary<string, object?> {
                        ["source"] = "rematch",
                        ["rematchIndex"] = r,
                        ["rematchSlots"] = kv.Value,
                        ["mapBank"] = bank,
                        ["mapNumber"] = mapNumber,
                        ["mapName"] = mapName,
                     };
                     if (!byTrainer.TryGetValue(kv.Key, out var bucket)) byTrainer[kv.Key] = bucket = new();
                     bucket.Add(use);
                  }
               }
            }
         } catch { }

         return byTrainer;
      }

      // Read the trainerbattle command's text-pointer args and map them onto the use dict.
      // First arg starts at address + LineCode.Count; advance by each arg's byte length.
      private static void ReadTextArgs(IDataModel model, ScriptSpot spot, Dictionary<string, object?> use) {
         int offset = spot.Address + spot.Line.LineCode.Count;
         foreach (var arg in spot.Line.Args) {
            try {
               if (arg.PointerType == ExpectedPointerType.Text) {
                  int dest = model.ReadPointer(offset);
                  string text = ReadPcsString(model, dest);
                  switch (arg.Name) {
                     case "start": use["introText"] = text; break;
                     case "playerwin": use["winText"] = text; break;
                     case "playerlose": use["loseText"] = text; break;
                  }
               }
            } catch { }
            offset += arg.Length(model, offset);
         }
      }

      // Decode the PCS string at `address` to a readable string (null on bad pointer / empty).
      private static string ReadPcsString(IDataModel model, int address) {
         if (address < 0 || address >= model.Count) return null;
         int length = PCSString.ReadString(model.RawData, address, true);
         if (length < 0) return null;
         var raw = model.TextConverter.Convert(model.RawData, address, length)?.Trim('"');
         return string.IsNullOrEmpty(raw) ? raw : ReadableText(raw);
      }

      // Command address as bare uppercase hex (min 6 digits, no 0x / no brackets), e.g. 1A93C9.
      public static string FormatOffset(int address) => address.ToString("X6");

      // The map-name table (data.maps.names) stores names either inline (RSE) or as a
      // pointer-to-text (FRLG: [name<"">]). ModelArrayElement.GetStringValue handles both,
      // so read each row through whichever text field the table actually has.
      private static List<string> MapNameColumn(IDataModel model) {
         var list = new List<string>();
         var table = model.GetTableModel(HardcodeTablesModel.MapNameTable);
         if (table == null) return list;
         var field = table.Run.ElementContent
            .FirstOrDefault(s => (s.Type == ElementContentType.PCS || s.Type == ElementContentType.Pointer) && !string.IsNullOrEmpty(s.Name))?.Name;
         for (int i = 0; i < table.Count; i++) {
            string v = null;
            if (field != null) { try { v = table[i].GetStringValue(field); } catch { } }
            list.Add(string.IsNullOrEmpty(v) ? null : ReadableText(v));
         }
         return list;
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
               member["moves"] = DefaultMoveIds(model, species, level).Select(id => Name(moveNames, id)).ToList();
            }
            party.Add(member);
         }
         return party;
      }

      /// <summary>
      /// The default moves a non-hardcoded party member would have: distinct moves only
      /// (each kept at its most-recent occurrence), the (up to) 4 with the highest level
      /// at or below the mon's level. Selected by level value — not learnset order — and
      /// not padded, so a mon with fewer than 4 eligible moves gets fewer.
      /// </summary>
      public static IReadOnlyList<int> DefaultMoveIds(IDataModel model, int species, int level) {
         // most-recent occurrence (highest level, then latest position) of each distinct move
         var best = new Dictionary<int, (int level, int pos)>();
         var learnset = TrainerPokemonTeamRun.GetLevelUpLearnset(model, species);
         for (int pos = 0; pos < learnset.Count; pos++) {
            var (move, lv) = learnset[pos];
            if (move == 0 || lv > level) continue;
            if (!best.TryGetValue(move, out var cur) || lv > cur.level || (lv == cur.level && pos > cur.pos))
               best[move] = (lv, pos);
         }
         return best
            .OrderBy(kv => kv.Value.level).ThenBy(kv => kv.Value.pos) // ascending by level
            .TakeLast(4)                                              // keep the 4 highest
            .Select(kv => kv.Key)
            .ToList();
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
