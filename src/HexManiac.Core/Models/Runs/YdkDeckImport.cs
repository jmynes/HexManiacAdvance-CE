using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// Imports EDOPro/Duelingbook-style .ydk deck files into a table whose card field is an
   /// ArrayRunEnumSegment with a PasswordTableHint (see ArrayRunEnumSegment.PasswordTableHint).
   /// Each .ydk line is an 8-digit card password. Passwords are stored in ROM as 4-byte packed
   /// BCD, little-endian, which (since every BCD digit is also a valid hex digit) means a
   /// password's bytes are exactly its text parsed as hex - so resolution is just "parse as hex,
   /// find the matching value in the passwords table, use that index."
   /// </summary>
   public static class YdkDeckImport {
      public readonly record struct Result(int Imported, int Skipped);

      public static Result Import(IDataModel model, ModelDelta token, ITableRun table, ArrayRunEnumSegment segment, string ydkText) {
         var passwordsTable = model.GetTable(segment.PasswordTableHint);
         var (main, extra, side) = ParseSections(ydkText);
         var anchorName = model.GetAnchorFromAddress(-1, table.Start) ?? string.Empty;
         var passwords = SelectSection(anchorName, segment.Name, main, extra, side);
         if (passwordsTable == null) return new Result(0, passwords.Count);

         // table.ElementCount/ElementLength assume one fixed-size row per element, which only
         // covers a repeating ArrayRun/TableStreamRun (one card field per row). A StructRun
         // instead has ElementCount 1 with this same field repeated many times within
         // ElementContent (one per InlineArraySegment repetition) - so find every occurrence by
         // name across every row, rather than assuming exactly one slot per row.
         var slots = FindSlots(table, segment);
         var passwordLength = passwordsTable.ElementContent[0].Length;

         int imported = 0, skipped = 0;
         var count = Math.Min(passwords.Count, slots.Count);
         for (int i = 0; i < count; i++) {
            if (!int.TryParse(passwords[i], NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var passwordValue)) { skipped++; continue; }

            var matchIndex = -1;
            for (int p = 0; p < passwordsTable.ElementCount; p++) {
               if (model.ReadMultiByteValue(passwordsTable.Start + p * passwordsTable.ElementLength, passwordLength) != passwordValue) continue;
               matchIndex = p;
               break;
            }
            if (matchIndex == -1) { skipped++; continue; }

            // data.cards.passwords has no name field of its own - its row count comes from a
            // named list (e.g. Format = '[password::|h]cardstatsnames'), which is exactly what
            // gives it per-row ElementNames labels (the same mechanism species/move tables use
            // to label rows with no literal text field). cardnames is a *different* list, so the
            // matched password's row index isn't meaningful there - resolve by name instead.
            var cardNames = passwordsTable.ElementNames;
            if (matchIndex >= cardNames.Count || !segment.TryParse(model, cardNames[matchIndex], out var finalIndex)) { skipped++; continue; }

            model.WriteMultiByteValue(slots[i], segment.Length, token, finalIndex);
            imported++;
         }

         return new Result(imported, skipped);
      }

      private static List<int> FindSlots(ITableRun table, ArrayRunEnumSegment segment) {
         var slots = new List<int>();
         for (int row = 0; row < table.ElementCount; row++) {
            var offset = table.Start + row * table.ElementLength;
            foreach (var seg in table.ElementContent) {
               if (seg.Name == segment.Name && seg is ArrayRunEnumSegment enumSeg && enumSeg.PasswordTableHint == segment.PasswordTableHint) {
                  slots.Add(offset);
               }
               offset += seg.Length;
            }
         }
         return slots;
      }

      private static List<string> SelectSection(string anchorName, string segmentName, List<string> main, List<string> extra, List<string> side) {
         // an anonymous struct (reached only by following a pointer) has no anchor name of its
         // own, so fall back to the clicked field's name - "main"/"extra" are exactly the field
         // names used for this purpose, matching the .ydk section they should pull from.
         foreach (var name in new[] { anchorName, segmentName }) {
            if (name.Contains("main", StringComparison.OrdinalIgnoreCase)) return main;
            if (name.Contains("extra", StringComparison.OrdinalIgnoreCase)) return extra;
            if (name.Contains("side", StringComparison.OrdinalIgnoreCase)) return side;
         }
         return main.Concat(extra).Concat(side).ToList();
      }

      private static (List<string> main, List<string> extra, List<string> side) ParseSections(string ydkText) {
         var main = new List<string>();
         var extra = new List<string>();
         var side = new List<string>();
         var current = main;

         foreach (var rawLine in ydkText.Split('\n')) {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("#main", StringComparison.OrdinalIgnoreCase)) { current = main; continue; }
            if (line.Equals("#extra", StringComparison.OrdinalIgnoreCase)) { current = extra; continue; }
            if (line.Equals("!side", StringComparison.OrdinalIgnoreCase)) { current = side; continue; }
            if (line.StartsWith("#") || line.StartsWith("!")) continue; // other comments/headers
            if (!line.All(char.IsDigit)) continue; // not a password line
            current.Add(line);
         }

         return (main, extra, side);
      }
   }
}
