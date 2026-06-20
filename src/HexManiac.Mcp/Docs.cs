using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace HavenSoft.HexManiac.Mcp;

public static class Docs {
   private static string? _cache;
   public static string Guide() {
      if (_cache != null) return _cache;
      var asm = Assembly.GetExecutingAssembly();
      var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("MCP.md", StringComparison.OrdinalIgnoreCase));
      if (name == null) return _cache = "MCP guide resource not found (build misconfiguration).";
      using var s = asm.GetManifestResourceStream(name)!;
      using var r = new StreamReader(s);
      return _cache = r.ReadToEnd();
   }
   // Return the section whose ## heading contains `topic` (case-insensitive),
   // through to the next same-or-higher heading; else a "sections:" listing.
   public static string Section(string topic) {
      var guide = Guide();
      var lines = guide.Replace("\r\n", "\n").Split('\n');
      var headings = lines.Where(l => l.StartsWith("## ")).Select(l => l.TrimStart('#', ' ')).ToList();
      int start = -1;
      for (int i = 0; i < lines.Length; i++) {
         if (lines[i].StartsWith("## ") && lines[i].ToLowerInvariant().Contains(topic.ToLowerInvariant())) { start = i; break; }
      }
      if (start < 0) return $"No section matches '{topic}'. Sections: {string.Join(", ", headings)}. Call help with no topic for the full guide.";
      int end = lines.Length;
      for (int i = start + 1; i < lines.Length; i++) { if (lines[i].StartsWith("## ")) { end = i; break; } }
      return string.Join("\n", lines.Skip(start).Take(end - start));
   }
}
