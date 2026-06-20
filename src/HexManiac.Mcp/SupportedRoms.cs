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
      public static object Match(string gameCode, string md5, string sha1, string crc32) {
         using var doc = JsonDocument.Parse(Json());
         JsonElement? byCode = null, byMd5 = null;
         foreach (var section in new[] { "roms", "notSupported", "alsoSupported" }) {
            if (!doc.RootElement.TryGetProperty(section, out var arr)) continue;
            foreach (var e in arr.EnumerateArray()) {
               if (e.TryGetProperty("headerCode", out var c) && string.Equals(c.GetString(), gameCode, StringComparison.OrdinalIgnoreCase)) byCode = e.Clone();
               if (e.TryGetProperty("md5", out var m) && string.Equals(m.GetString(), md5, StringComparison.OrdinalIgnoreCase)) byMd5 = e.Clone();
            }
         }
         string Get(JsonElement? el, string p) => el is JsonElement j && j.TryGetProperty(p, out var v) ? v.GetString() : null;
         var baseGame = Get(byCode, "game");
         var note = baseGame == null
            ? $"Unrecognized header code '{gameCode}'; HMA opens this as a plain hex editor."
            : (byMd5 != null ? "Clean No-Intro dump." : "Header matches a supported base, but the bytes don't match any known clean dump — edited / romhack.");
         return new {
            ok = true,
            headerCode = gameCode,
            baseGame,
            revision = Get(byCode, "revision"),
            hmaSupport = Get(byCode, "hmaSupport"),
            isCleanDump = byMd5 != null,
            matchedNoIntro = Get(byMd5, "noIntroName"),
            md5, sha1, crc32, note
         };
      }

      // Return the rom entries whose headerCode contains `code` (case-insensitive), as a JSON array string.
      // Searches roms, alsoSupported, and notSupported sections (any present).
      public static string Lookup(string code) {
         using var doc = JsonDocument.Parse(Json());
         var matches = new System.Collections.Generic.List<string>();
         foreach (var section in new[] { "roms", "alsoSupported", "notSupported" }) {
            if (!doc.RootElement.TryGetProperty(section, out var arr)) continue;
            foreach (var e in arr.EnumerateArray()) {
               if (e.TryGetProperty("headerCode", out var c) && c.GetString() is string s
                   && s.IndexOf(code, StringComparison.OrdinalIgnoreCase) >= 0)
                  matches.Add(e.GetRawText());
            }
         }
         return "[" + string.Join(",", matches) + "]";
      }
   }
}
