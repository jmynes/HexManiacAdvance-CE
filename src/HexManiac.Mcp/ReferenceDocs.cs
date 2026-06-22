using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

// Serves HexManiacAdvance's bundled reference files (resources/*.txt) - the same data the engine uses
// for script/asm parsing and autocomplete - so an agent can look up commands, constants, anchors, the
// text encoding, ARM, and the tutorial links without a repo checkout. Read-only; works headless or live.
public static class ReferenceDocs {
   private static readonly Dictionary<string, (string file, string desc)> Kinds = new(StringComparer.OrdinalIgnoreCase) {
      ["script"]    = ("scriptReference.txt", "overworld script (XSE) commands"),
      ["battle"]    = ("battleScriptReference.txt", "battle script commands"),
      ["ai"]        = ("battleAIScriptReference.txt", "battle-AI script commands"),
      ["animation"] = ("animationScriptReference.txt", "battle-animation script commands"),
      ["constants"] = ("constantReference.txt", "named constants"),
      ["arm"]       = ("armReference.txt", "ARM/THUMB assembly reference"),
      ["pcs"]       = ("pcsReference.txt", "PCS in-game text encoding"),
      ["tables"]    = ("tableReference.txt", "table anchors + per-game offsets"),
      ["doc"]       = ("docReference.txt", "links to HMA's basic/advanced/scripting tutorials"),
   };
   private static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase) {
      ["scripts"] = "script", ["overworld"] = "script", ["xse"] = "script",
      ["bse"] = "battle", ["battlescript"] = "battle",
      ["tse"] = "ai", ["battleai"] = "ai",
      ["ase"] = "animation", ["anim"] = "animation",
      ["const"] = "constants", ["constant"] = "constants",
      ["thumb"] = "arm", ["asm"] = "arm", ["assembly"] = "arm",
      ["text"] = "pcs", ["encoding"] = "pcs",
      ["table"] = "tables",
      ["docs"] = "doc", ["tutorials"] = "doc", ["documentation"] = "doc",
   };

   public static object Lookup(string? kind, string? query, int maxLines) {
      if (string.IsNullOrWhiteSpace(kind)) {
         return new Dictionary<string, object?> {
            ["ok"] = true,
            ["kinds"] = Kinds.ToDictionary(k => k.Key, k => (object)k.Value.desc),
            ["hint"] = "Call reference with a kind (e.g. 'script') and an optional query (e.g. 'applymovement'). For the loaded ROM's specials list use list_specials.",
         };
      }
      var k = kind.Trim();
      if (Alias.TryGetValue(k, out var canon)) k = canon;
      if (!Kinds.TryGetValue(k, out var info))
         return RomAutomation.Err($"Unknown reference kind '{kind}'. Valid kinds: {string.Join(", ", Kinds.Keys)} (plus aliases).");
      var path = Resolve(info.file);
      if (path == null) return RomAutomation.Err($"Reference file not found next to the server: resources/{info.file}");

      var lines = File.ReadAllLines(path);
      bool filtered = !string.IsNullOrWhiteSpace(query);
      var body = filtered
         ? lines.Where(l => l.IndexOf(query!.Trim(), StringComparison.OrdinalIgnoreCase) >= 0).ToList()
         : lines.ToList();
      int total = body.Count;
      if (maxLines <= 0) maxLines = 400;
      bool truncated = total > maxLines;
      if (truncated) body = body.Take(maxLines).ToList();

      return new Dictionary<string, object?> {
         ["ok"] = true,
         ["kind"] = k,
         ["file"] = "resources/" + info.file,
         ["description"] = info.desc,
         ["query"] = filtered ? query : null,
         ["matchCount"] = total,
         ["returned"] = body.Count,
         ["truncated"] = truncated,
         ["text"] = string.Join("\n", body),
      };
   }

   // resources/ is copied next to the exe (see the .csproj). Try there, then the working dir.
   private static string? Resolve(string file) {
      foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() }) {
         if (string.IsNullOrEmpty(dir)) continue;
         var p = Path.Combine(dir, "resources", file);
         if (File.Exists(p)) return p;
      }
      var rel = Path.Combine("resources", file);
      return File.Exists(rel) ? rel : null;
   }
}
