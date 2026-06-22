using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Map;
using HavenSoft.HexManiac.Core.ViewModels.Map;

namespace HavenSoft.HexManiac.Core.Models {
   // Walks every top-level map script + every signpost for the item sources the item table can't
   // give you: where each item is BOUGHT (pokemart product lists) and FOUND (item-ball / found-item
   // scripts, NPC gift scripts, raw additem, and hidden-item signposts). Same map walk the
   // encounter/trainer exports use (TrainerTeamExport.WalkTopLevelMapScripts), with the command
   // opcodes resolved BY NAME from the engine so it keeps working on other base games and romhacks.
   public static class ItemLocationExport {
      // The find.item / npc.item macros load the item into VAR_0x8000 and the count into VAR_0x8001,
      // then callstd 1 (found) or 0 (npc gift). These var ids are stable Gen-3 engine conventions.
      private const int Var_Item = 0x8000, Var_ItemCount = 0x8001;
      private const int Std_NpcItem = 0, Std_FindItem = 1;

      public static object Export(IDataModel model, ScriptParser parser, string outPath) {
         if (parser == null) return RomAutomation.Err("export_item_locations needs the script parser (open a ROM with the code tool / live GUI).");
         var itemNames = TrainerTeamExport.NameColumn(model, HardcodeTablesModel.ItemsTableName);
         var mapNames = TrainerTeamExport.MapNameColumn(model);

         var sites = CollectSites(model, parser, itemNames, mapNames);

         var byItem = new SortedDictionary<int, List<Dictionary<string, object?>>>();
         foreach (var s in sites) {
            int id = (int)s["itemId"]!;
            if (!byItem.TryGetValue(id, out var bucket)) byItem[id] = bucket = new();
            bucket.Add(s);
         }
         var byItemOut = byItem.ToDictionary(kv => kv.Key.ToString(), kv => (object?)kv.Value);

         int Count(string k) => sites.Count(s => (string)s["kind"] == k);

         var payload = new Dictionary<string, object?> {
            ["source"] = "HexManiacAdvance MCP - map-script + signpost walk for item sources (pokemart product lists, find.item/npc.item callstd scripts, additem, hidden-item signposts). Opcodes resolved by name, so it works across base games and romhacks.",
            ["notes"] = new Dictionary<string, object?> {
               ["kind"] = "mart = item appears in a pokemart product list (buyable; the buy price is in data.items.stats). field = an item-ball / found-item script (copyvarifnotzero VAR_0x8000 then callstd 1). hidden = a hidden-item signpost (event kind 5-7, item baked into the event data). gift = an NPC give-item script (callstd 0). scripted = a raw additem command.",
               ["coverage"] = "Only top-level map scripts (object/script/signpost events + map-header scripts) and hidden-item signposts are walked; hand-written ASM and item ids loaded from a variable are not resolved. Game Corner coin prizes are export_coin_prizes; held items on wild/trainer mons are in the species/trainer tables.",
               ["scriptOffset"] = "Bare uppercase hex address of the command (or of the signpost event, for hidden items).",
               ["quantity"] = "Count baked into the script/event; omitted for marts (you buy any amount).",
            },
            ["martCount"] = Count("mart"), ["fieldCount"] = Count("field"), ["hiddenCount"] = Count("hidden"),
            ["giftCount"] = Count("gift"), ["scriptedCount"] = Count("scripted"),
            ["siteCount"] = sites.Count,
            ["itemsCovered"] = byItem.Count,
            ["byItem"] = byItemOut,
            ["sites"] = sites,
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions {
            WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
         }));
         return new Dictionary<string, object?> {
            ["ok"] = true, ["martCount"] = Count("mart"), ["fieldCount"] = Count("field"), ["hiddenCount"] = Count("hidden"),
            ["giftCount"] = Count("gift"), ["scriptedCount"] = Count("scripted"), ["siteCount"] = sites.Count,
            ["itemsCovered"] = byItem.Count, ["path"] = outPath,
         };
      }

      private static List<Dictionary<string, object?>> CollectSites(IDataModel model, ScriptParser parser,
            List<string> itemNames, List<string> mapNames) {
         var sites = new List<Dictionary<string, object?>>();
         var seen = new HashSet<string>();  // dedupe a site reached from more than one event
         var mapNameByLocation = new Dictionary<(int bank, int map), string>();

         // Resolve commands by name for THIS game (romhack-safe).
         byte pokemart = parser.CommandCode("pokemart") ?? 0;
         // opcode 0x1A: HMA's primary name is 'setorcopyvar'; 'copyvarifnotzero' is its old alias.
         // The find.item / npc.item macros load the item via this command into VAR_0x8000.
         byte copyvarifnotzero = parser.CommandCode("setorcopyvar") ?? parser.CommandCode("copyvarifnotzero") ?? 0;
         byte callstd = parser.CommandCode("callstd") ?? 0;
         byte additem = parser.CommandCode("additem") ?? 0;
         // We filter on the *first* opcode of each item source. find.item / npc.item are MACROS that
         // HMA decodes as one line starting with this setorcopyvar (the item load); the trailing
         // callstd is consumed inside that line, not a separate spot - so we read it from the macro
         // layout (below), and callstd is NOT in the filter.
         var filter = new List<byte>();
         foreach (var op in new[] { pokemart, copyvarifnotzero, additem }) if (op != 0) filter.Add(op);
         if (filter.Count == 0) return sites;  // engine doesn't define any of the commands we rely on
         var filterArray = filter.ToArray();

         string ItemName(int id) => id > 0 && id < itemNames.Count && !string.IsNullOrEmpty(itemNames[id]) ? itemNames[id] : $"#{id}";

         void Add(string kind, int itemId, int quantity, int bank, int mapNumber, string mapName, int offset, int? x = null, int? y = null) {
            if (itemId <= 0 || itemId >= itemNames.Count) return; // ITEM_NONE / var-loaded / stray parse
            if (!seen.Add($"{kind}:{itemId}:{offset}")) return;
            var d = new Dictionary<string, object?> {
               ["kind"] = kind,
               ["itemId"] = itemId,
               ["item"] = ItemName(itemId),
               ["mapBank"] = bank,
               ["mapNumber"] = mapNumber,
               ["mapName"] = mapName,
               ["scriptOffset"] = TrainerTeamExport.FormatOffset(offset),
            };
            if (quantity > 0) d["quantity"] = quantity;
            if (x.HasValue) d["x"] = x.Value;
            if (y.HasValue) d["y"] = y.Value;
            sites.Add(d);
         }

         void Record(int scriptStart, int bank, int mapNumber, string mapName) {
            if (scriptStart < 0 || scriptStart >= model.Count) return;
            IEnumerable<ScriptSpot> spots;
            try { spots = Flags.GetAllScriptSpots(model, parser, new[] { scriptStart }, filterArray).ToList(); }
            catch { return; }
            foreach (var spot in spots) {
               try {
                  byte op = model[spot.Address];
                  if (copyvarifnotzero != 0 && op == copyvarifnotzero) {
                     // find.item / npc.item: setorcopyvar VAR_0x8000=item [setorcopyvar VAR_0x8001=count
                     // [setorcopyvar VAR_0x8002=song]] callstd <std>. Each setorcopyvar is 5 bytes
                     // (1A + var:u16 + source:u16). Only the item load (VAR_0x8000) starts a source.
                     int a = spot.Address;
                     if (model.ReadMultiByteValue(a + 1, 2) != Var_Item) continue;
                     int item = model.ReadMultiByteValue(a + 3, 2);
                     int qty = 0, p = a + 5;
                     while (p + 5 <= model.Count && model[p] == copyvarifnotzero) {  // skip count / song loads
                        if (model.ReadMultiByteValue(p + 1, 2) == Var_ItemCount) qty = model.ReadMultiByteValue(p + 3, 2);
                        p += 5;
                     }
                     if (callstd != 0 && p + 1 < model.Count && model[p] == callstd) {
                        int std = model[p + 1];
                        if (std == Std_FindItem) Add("field", item, qty, bank, mapNumber, mapName, a);
                        else if (std == Std_NpcItem) Add("gift", item, qty, bank, mapNumber, mapName, a);
                     }
                  } else if (pokemart != 0 && op == pokemart) {
                     int listPtr = model.ReadPointer(spot.Address + 1);  // products<mart>: 0000-terminated u16 list
                     if (listPtr > 0 && listPtr < model.Count) {
                        for (int a = listPtr, guard = 0; guard < 256; a += 2, guard++) {
                           int itemId = model.ReadMultiByteValue(a, 2);
                           if (itemId == 0 || itemId == 0xFFFF) break;
                           Add("mart", itemId, 0, bank, mapNumber, mapName, spot.Address);
                        }
                     }
                  } else if (additem != 0 && op == additem) {
                     Add("scripted", model.ReadMultiByteValue(spot.Address + 1, 2),
                        model.ReadMultiByteValue(spot.Address + 3, 2), bank, mapNumber, mapName, spot.Address);
                  }
               } catch { }
            }
         }

         TrainerTeamExport.WalkTopLevelMapScripts(model, mapNames, Record, mapNameByLocation);

         // Hidden items live in signpost event DATA (kind 5-7), not scripts - read them directly.
         try {
            var banks = AllMapsModel.Create(model, default);
            for (int b = 0; b < banks.Count; b++) {
               MapBankModel? bank;
               try { bank = banks[b]; } catch { continue; }
               if (bank == null) continue;
               for (int m = 0; m < bank.Count; m++) {
                  MapModel? map;
                  try { map = bank[m]; } catch { continue; }
                  if (map == null) continue;
                  mapNameByLocation.TryGetValue((b, m), out var mapName);
                  try {
                     foreach (var sp in map.Events.Signposts) {
                        if (sp == null || !sp.IsHiddenItem) continue;
                        int amount = (sp.Arg >> 24) & 0x7F;  // arg = item | flagId<<16 | quantity<<24
                        Add("hidden", sp.ItemValue, amount, b, m, mapName, sp.Element.Start, sp.X, sp.Y);
                     }
                  } catch { }
               }
            }
         } catch { }

         return sites;
      }
   }
}
