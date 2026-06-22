using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;

namespace HavenSoft.HexManiac.Core.Models {
   // Sprite/palette import & export for the MCP. Sprites round-trip as 8-bit INDEXED PNGs (palette
   // indices preserved exactly); palettes as JSON hex-color lists. Address by anchor name or hex.
   public static class SpriteIO {
      private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

      // ---- GBA 15-bit color (HMA layout: R<<10 | G<<5 | B, 5 bits each) <-> 8-bit RGB ----
      private static (byte r, byte g, byte b) ToRgb(short c) {
         int r5 = (c >> 10) & 0x1F, g5 = (c >> 5) & 0x1F, b5 = c & 0x1F;
         return ((byte)((r5 << 3) | (r5 >> 2)), (byte)((g5 << 3) | (g5 >> 2)), (byte)((b5 << 3) | (b5 >> 2)));
      }
      private static short ToShort(byte r, byte g, byte b) => (short)(((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));
      private static string Hex((byte r, byte g, byte b) c) => $"#{c.r:X2}{c.g:X2}{c.b:X2}";

      // Resolve to the address of a sprite/palette run. Accepts: an anchor name, a hex address
      // ("0x..."/bare hex), or "<table>/<index>" (HMA goto syntax) - which follows the pointer in that
      // table element, so e.g. graphics.pokemon.sprites.front/1 reaches Bulbasaur's front sprite.
      private static int Resolve(IDataModel model, string spec) {
         if (string.IsNullOrWhiteSpace(spec)) return -1;
         spec = spec.Trim();
         int index = -1, slash = spec.IndexOf('/');
         if (slash >= 0) {
            if (!int.TryParse(spec.Substring(slash + 1), out index)) index = -1;
            spec = spec.Substring(0, slash);
         }
         int addr;
         var anchor = spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? -1 : model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, spec);
         if (anchor >= 0 && anchor < model.Count) {
            addr = anchor;
         } else {
            var hex = spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? spec.Substring(2) : spec;
            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out addr)) return -1;
         }
         if (index >= 0 && model.GetNextRun(addr) is ITableRun table && index < table.ElementCount) {
            int dest = model.ReadPointer(table.Start + index * table.ElementLength);  // the sprite/palette pointer is the element's first field
            if (dest >= 0 && dest < model.Count) return dest;
         }
         return addr;
      }

      private static ISpriteRun GetSprite(IDataModel model, string spec, out string err) {
         err = null;
         int addr = Resolve(model, spec);
         if (addr < 0) { err = $"Couldn't resolve sprite '{spec}' (use an anchor name or hex address)."; return null; }
         if (model.GetNextRun(addr) is ISpriteRun s && s.Start <= addr) return s;
         err = $"No sprite at '{spec}' (0x{addr:X6}). list_tables shows anchors; the address must point at a sprite/lz-sprite run.";
         return null;
      }
      private static IPaletteRun GetPalette(IDataModel model, string spec, out string err) {
         err = null;
         int addr = Resolve(model, spec);
         if (addr < 0) { err = $"Couldn't resolve palette '{spec}'."; return null; }
         if (model.GetNextRun(addr) is IPaletteRun p && p.Start <= addr) return p;
         err = $"No palette at '{spec}' (0x{addr:X6}).";
         return null;
      }

      // resolve the RGB palette to render a sprite with: explicit spec, else HMA's auto-pairing, else a grayscale ramp.
      private static List<(byte r, byte g, byte b)> ResolvePalette(IDataModel model, ISpriteRun sprite, string paletteSpec, int palettePage, out IPaletteRun palRun) {
         palRun = null;
         IReadOnlyList<short> colors = null;
         if (!string.IsNullOrWhiteSpace(paletteSpec)) {
            palRun = GetPalette(model, paletteSpec, out _);
         } else {
            palRun = sprite.FindRelatedPalettes(model).FirstOrDefault();
         }
         if (palRun != null) {
            int page = Math.Max(0, Math.Min(palettePage, palRun.Pages - 1));
            colors = palRun.GetPalette(model, page);
         }
         if (colors != null) return colors.Select(ToRgb).ToList();
         int count = 1 << sprite.SpriteFormat.BitsPerPixel;       // no palette (e.g. 1-/2-bpp): grayscale ramp
         return Enumerable.Range(0, count).Select(i => { byte v = (byte)(count <= 1 ? 0 : i * 255 / (count - 1)); return (v, v, v); }).ToList();
      }

      public static object ExportSprite(IDataModel model, string spriteSpec, string paletteSpec, int page, int palettePage, string outPath) {
         var sprite = GetSprite(model, spriteSpec, out var err);
         if (sprite == null) return RomAutomation.Err(err);
         page = Math.Max(0, Math.Min(page, Math.Max(0, sprite.Pages - 1)));
         var px = sprite.GetPixels(model, page, -1);              // [width, height]
         int w = px.GetLength(0), h = px.GetLength(1);
         var palette = ResolvePalette(model, sprite, paletteSpec, palettePage, out var palRun);
         int maxIndex = palette.Count - 1;
         var pixels = new int[h, w];                              // PNG wants [y, x]
         for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels[y, x] = Math.Max(0, Math.Min(px[x, y], maxIndex));
         File.WriteAllBytes(outPath, PngCodec.EncodeIndexed(pixels, palette, transparentIndex: 0));
         return new Dictionary<string, object?> {
            ["ok"] = true, ["path"] = outPath, ["address"] = $"0x{sprite.Start:X6}",
            ["width"] = w, ["height"] = h, ["bitsPerPixel"] = sprite.SpriteFormat.BitsPerPixel,
            ["page"] = page, ["pages"] = sprite.Pages, ["format"] = "indexed PNG (index 0 transparent)",
            ["palette"] = palRun == null ? "grayscale (no palette found)" : $"0x{palRun.Start:X6}",
            ["paletteColors"] = palette.Count,
         };
      }

      public static object ImportSprite(IDataModel model, Func<ModelDelta> token, string spriteSpec, string inPath, int page, bool applyPalette, string paletteSpec, int palettePage) {
         var sprite = GetSprite(model, spriteSpec, out var err);
         if (sprite == null) return RomAutomation.Err(err);
         if (!File.Exists(inPath)) return RomAutomation.Err($"File not found: {inPath}");
         page = Math.Max(0, Math.Min(page, Math.Max(0, sprite.Pages - 1)));
         var img = PngCodec.Decode(File.ReadAllBytes(inPath));
         var px = sprite.GetPixels(model, page, -1);
         int w = px.GetLength(0), h = px.GetLength(1);
         if (img.Width != w || img.Height != h)
            return RomAutomation.Err($"Image is {img.Width}x{img.Height} but the sprite is {w}x{h}. Match the dimensions (export it first to see them).");

         var palRun = ResolvePalette(model, sprite, paletteSpec, palettePage, out var pr) is var pal ? pr : null;
         var newPixels = new int[w, h];                           // [width, height] for SetPixels
         if (img.ColorType == 3) {                                // indexed: use the indices directly
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) newPixels[x, y] = img.Indices[y, x];
            if (applyPalette && pr != null && img.Palette != null) {
               int pcount = pr.GetPalette(model, Math.Max(0, Math.Min(palettePage, pr.Pages - 1))).Count;
               var shorts = Enumerable.Range(0, pcount).Select(i => i < img.Palette.Count ? ToShort(img.Palette[i].r, img.Palette[i].g, img.Palette[i].b) : (short)0).ToList();
               pr.SetPalette(model, token(), Math.Max(0, Math.Min(palettePage, pr.Pages - 1)), shorts);
            }
         } else {                                                 // truecolor: nearest-match to the sprite's palette
            var palette = ResolvePalette(model, sprite, paletteSpec, palettePage, out _);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) newPixels[x, y] = Nearest(img.Rgb[y, x], palette);
         }
         sprite.SetPixels(model, token(), page, newPixels);
         return new Dictionary<string, object?> {
            ["ok"] = true, ["address"] = $"0x{sprite.Start:X6}", ["width"] = w, ["height"] = h,
            ["page"] = page, ["source"] = img.ColorType == 3 ? "indexed PNG (indices used directly)" : "truecolor PNG (nearest-matched to palette)",
            ["paletteApplied"] = applyPalette && img.ColorType == 3 && pr != null,
         };
      }

      private static int Nearest((byte r, byte g, byte b) c, List<(byte r, byte g, byte b)> palette) {
         int best = 0, bestD = int.MaxValue;
         for (int i = 0; i < palette.Count; i++) {
            int dr = c.r - palette[i].r, dg = c.g - palette[i].g, db = c.b - palette[i].b;
            int d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = i; }
         }
         return best;
      }

      public static object ExportPalette(IDataModel model, string paletteSpec, int page, string outPath) {
         var pal = GetPalette(model, paletteSpec, out var err);
         if (pal == null) return RomAutomation.Err(err);
         bool allPages = page < 0;
         var pages = new List<object>();
         for (int pg = 0; pg < pal.Pages; pg++) {
            if (!allPages && pg != page) continue;
            pages.Add(new Dictionary<string, object?> { ["page"] = pg, ["colors"] = pal.GetPalette(model, pg).Select(c => Hex(ToRgb(c))).ToList() });
         }
         var payload = new Dictionary<string, object?> {
            ["address"] = $"0x{pal.Start:X6}", ["bits"] = pal.PaletteFormat.Bits, ["pages"] = pal.Pages, ["palettes"] = pages,
            ["note"] = "Colors are #RRGGBB (8-bit), quantized to GBA 5-bit on import.",
         };
         File.WriteAllText(outPath, JsonSerializer.Serialize(payload, Json));
         return new Dictionary<string, object?> { ["ok"] = true, ["path"] = outPath, ["address"] = $"0x{pal.Start:X6}", ["pagesExported"] = pages.Count };
      }

      public static object ImportPalette(IDataModel model, Func<ModelDelta> token, string paletteSpec, int page, IReadOnlyList<string> colors, string inPath) {
         var pal = GetPalette(model, paletteSpec, out var err);
         if (pal == null) return RomAutomation.Err(err);
         if (colors == null && !string.IsNullOrEmpty(inPath)) {
            if (!File.Exists(inPath)) return RomAutomation.Err($"File not found: {inPath}");
            using var doc = JsonDocument.Parse(File.ReadAllText(inPath));
            var palettes = doc.RootElement.GetProperty("palettes");
            var pick = palettes.EnumerateArray().FirstOrDefault(e => e.GetProperty("page").GetInt32() == Math.Max(0, page));
            if (pick.ValueKind != JsonValueKind.Object) pick = palettes.EnumerateArray().First();
            colors = pick.GetProperty("colors").EnumerateArray().Select(c => c.GetString()).ToList();
         }
         if (colors == null || colors.Count == 0) return RomAutomation.Err("Provide colors (a list of #RRGGBB) or inPath to a palette JSON.");
         int pg = Math.Max(0, Math.Min(page, pal.Pages - 1));
         int slots = pal.GetPalette(model, pg).Count;
         var shorts = new List<short>();
         for (int i = 0; i < slots; i++) {
            if (i < colors.Count && TryHex(colors[i], out var r, out var g, out var b)) shorts.Add(ToShort(r, g, b));
            else shorts.Add(0);
         }
         pal.SetPalette(model, token(), pg, shorts);
         return new Dictionary<string, object?> { ["ok"] = true, ["address"] = $"0x{pal.Start:X6}", ["page"] = pg, ["colorsWritten"] = slots };
      }

      private static bool TryHex(string s, out byte r, out byte g, out byte b) {
         r = g = b = 0;
         if (string.IsNullOrWhiteSpace(s)) return false;
         s = s.Trim().TrimStart('#');
         if (s.Length != 6) return false;
         return byte.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, null, out r)
             && byte.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, null, out g)
             && byte.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, null, out b);
      }
   }
}
