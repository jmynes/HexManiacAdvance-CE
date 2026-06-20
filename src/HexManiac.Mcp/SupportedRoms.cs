using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace HavenSoft.HexManiac.Mcp {
   public static class SupportedRoms {
      private static string _cache;
      public static string Json() {
         if (_cache != null) return _cache;
         var asm = Assembly.GetExecutingAssembly();
         var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("supported-roms.json", StringComparison.OrdinalIgnoreCase));
         if (name == null) return _cache = "{\"error\":\"supported-roms.json resource not found (build misconfiguration).\"}";
         using var s = asm.GetManifestResourceStream(name)!;
         using var r = new StreamReader(s);
         return _cache = r.ReadToEnd();
      }
      // Return the rom entries whose headerCode contains `code` (case-insensitive), as a JSON array string.
      public static string Lookup(string code) {
         using var doc = JsonDocument.Parse(Json());
         if (!doc.RootElement.TryGetProperty("roms", out var roms)) return "[]";
         var matches = roms.EnumerateArray()
            .Where(e => e.TryGetProperty("headerCode", out var c) && c.GetString() is string s
                        && s.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
            .Select(e => e.GetRawText());
         return "[" + string.Join(",", matches) + "]";
      }
   }
}
