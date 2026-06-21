using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace HavenSoft.HexManiac.Core.Models {
   // Maps ROM species display names to canonical PokeAPI-style slugs, and records
   // the few Gen-3 species that have more than one PokeAPI form. Used to annotate
   // species-indexed table reads with a `slug` (+ `forms`) so ROM names line up
   // with external data sources. Examples:
   //   "MR. MIME"     -> "mr-mime"      (punctuation collapses, not a unique mon)
   //   "NIDORAN\sf"   -> "nidoran-f"    (gender symbols stay distinct from \sm)
   //   "FARFETCH'D"   -> "farfetchd"
   //   "HO-OH"        -> "ho-oh"
   //   "DEOXYS"       -> "deoxys"       (+ its four formes via Forms)
   public static class PokemonSpeciesNaming {
      // species slug -> its PokeAPI form slugs (only species that actually have >1)
      public static readonly IReadOnlyDictionary<string, string[]> Forms = new Dictionary<string, string[]> {
         ["deoxys"] = new[] { "deoxys-normal", "deoxys-attack", "deoxys-defense", "deoxys-speed" },
         ["castform"] = new[] { "castform", "castform-sunny", "castform-rainy", "castform-snowy" },
      };

      // Gen-3 header game code (first 4 chars) -> the Deoxys forme that game uses.
      private static readonly IReadOnlyDictionary<string, string> DeoxysFormeByGame = new Dictionary<string, string> {
         ["BPRE"] = "deoxys-attack",   // FireRed
         ["BPGE"] = "deoxys-defense",  // LeafGreen
         ["BPEE"] = "deoxys-speed",    // Emerald
         ["AXVE"] = "deoxys-normal",   // Ruby
         ["AXPE"] = "deoxys-normal",   // Sapphire
      };

      // Canonical lowercase slug for a ROM species name, or null when blank.
      public static string Slug(string romName) {
         if (string.IsNullOrWhiteSpace(romName)) return null;
         var s = romName
            .Replace("\\sf", " f").Replace("\\sm", " m")        // HMA gender-symbol escapes
            .Replace("♀", " f").Replace("♂", " m")    // raw ♀ / ♂
            .ToLowerInvariant()
            .Replace("'", "").Replace("’", "")             // farfetch'd -> farfetchd
            .Replace(".", " ");                                 // mr. mime -> mr  mime
         var sb = new StringBuilder(s.Length);
         foreach (var c in s) {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
         }
         var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
         return slug.Length == 0 ? null : slug;
      }

      // The PokeAPI form slugs for a species slug, or null if it is single-form.
      public static string[] FormsFor(string slug) =>
         slug != null && Forms.TryGetValue(slug, out var f) ? f : null;

      // The Deoxys forme used by a given ROM (formes differ per Gen-3 game), or null.
      public static string DeoxysFormeFor(string gameCode) {
         if (string.IsNullOrEmpty(gameCode)) return null;
         var key = gameCode.Length >= 4 ? gameCode.Substring(0, 4) : gameCode;
         return DeoxysFormeByGame.TryGetValue(key, out var f) ? f : null;
      }
   }
}
