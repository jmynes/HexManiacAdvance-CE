using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace HavenSoft.HexManiac.Core.Models {
   // Minimal, dependency-free PNG read/write for sprite import/export from the headless MCP (WPF has
   // its own imaging; Core does not). Writes 8-bit INDEXED PNGs (color type 3) with a PLTE + optional
   // tRNS. Reads indexed (3), truecolor (2) and truecolor+alpha (6), filters 0-4, non-interlaced.
   public static class PngCodec {
      private static readonly byte[] Signature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

      private static readonly uint[] CrcTable = BuildCrcTable();
      private static uint[] BuildCrcTable() {
         var t = new uint[256];
         for (uint n = 0; n < 256; n++) {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
         }
         return t;
      }
      private static uint Crc(byte[] data) {
         uint c = 0xFFFFFFFFu;
         foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
         return c ^ 0xFFFFFFFFu;
      }

      public sealed class Image {
         public int Width, Height;
         public int ColorType;                  // 2=RGB, 3=indexed, 6=RGBA
         public int[,] Indices;                 // [y,x] palette index (indexed images only)
         public (byte r, byte g, byte b)[,] Rgb; // [y,x] color (truecolor images)
         public List<(byte r, byte g, byte b)> Palette; // PLTE (indexed images)
      }

      // ---- encode: 8-bit indexed PNG ----
      // pixels[y,x] = palette index; palette = RGB entries; transparentIndex<0 disables tRNS.
      public static byte[] EncodeIndexed(int[,] pixels, IReadOnlyList<(byte r, byte g, byte b)> palette, int transparentIndex = 0) {
         int h = pixels.GetLength(0), w = pixels.GetLength(1);
         using var ms = new MemoryStream();
         ms.Write(Signature, 0, Signature.Length);

         var ihdr = new byte[13];
         WriteBE(ihdr, 0, (uint)w); WriteBE(ihdr, 4, (uint)h);
         ihdr[8] = 8;   // bit depth
         ihdr[9] = 3;   // color type: indexed
         // 10,11,12 = compression/filter/interlace = 0
         WriteChunk(ms, "IHDR", ihdr);

         var plte = new byte[palette.Count * 3];
         for (int i = 0; i < palette.Count; i++) { plte[i * 3] = palette[i].r; plte[i * 3 + 1] = palette[i].g; plte[i * 3 + 2] = palette[i].b; }
         WriteChunk(ms, "PLTE", plte);

         if (transparentIndex >= 0 && transparentIndex < palette.Count) {
            var trns = new byte[transparentIndex + 1];
            for (int i = 0; i < trns.Length; i++) trns[i] = (byte)255;
            trns[transparentIndex] = 0;
            WriteChunk(ms, "tRNS", trns);
         }

         // scanlines: each row prefixed with filter byte 0 (None)
         var raw = new byte[h * (w + 1)];
         int p = 0;
         for (int y = 0; y < h; y++) {
            raw[p++] = 0;
            for (int x = 0; x < w; x++) raw[p++] = (byte)(pixels[y, x] & 0xFF);
         }
         WriteChunk(ms, "IDAT", ZlibCompress(raw));
         WriteChunk(ms, "IEND", Array.Empty<byte>());
         return ms.ToArray();
      }

      public static Image Decode(byte[] data) {
         for (int i = 0; i < Signature.Length; i++) if (data[i] != Signature[i]) throw new InvalidDataException("Not a PNG file.");
         int pos = 8, width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
         var palette = new List<(byte, byte, byte)>();
         using var idat = new MemoryStream();
         while (pos + 8 <= data.Length) {
            int len = (int)ReadBE(data, pos); pos += 4;
            string type = System.Text.Encoding.ASCII.GetString(data, pos, 4); pos += 4;
            int dataStart = pos;
            if (type == "IHDR") {
               width = (int)ReadBE(data, dataStart); height = (int)ReadBE(data, dataStart + 4);
               bitDepth = data[dataStart + 8]; colorType = data[dataStart + 9]; interlace = data[dataStart + 12];
            } else if (type == "PLTE") {
               for (int i = 0; i < len; i += 3) palette.Add((data[dataStart + i], data[dataStart + i + 1], data[dataStart + i + 2]));
            } else if (type == "IDAT") {
               idat.Write(data, dataStart, len);
            } else if (type == "IEND") break;
            pos += len + 4; // skip data + CRC
         }
         if (interlace != 0) throw new NotSupportedException("Interlaced PNGs aren't supported - re-export without interlacing.");
         if (bitDepth != 8 && !(colorType == 3 && (bitDepth == 1 || bitDepth == 2 || bitDepth == 4)))
            throw new NotSupportedException($"Unsupported PNG bit depth {bitDepth} for color type {colorType}.");

         int channels = colorType switch { 2 => 3, 6 => 4, 0 => 1, 3 => 1, 4 => 2, _ => throw new NotSupportedException($"Unsupported PNG color type {colorType}.") };
         int bpp = Math.Max(1, channels * bitDepth / 8);                 // bytes per pixel for filtering
         int rowBytes = (width * channels * bitDepth + 7) / 8;
         var raw = Unfilter(ZlibDecompress(idat.ToArray()), height, rowBytes, bpp);

         var img = new Image { Width = width, Height = height, ColorType = colorType };
         if (colorType == 3) {
            img.Palette = palette;
            img.Indices = new int[height, width];
            for (int y = 0; y < height; y++) {
               int rowOff = y * rowBytes;
               for (int x = 0; x < width; x++) img.Indices[y, x] = ReadSample(raw, rowOff, x, bitDepth);
            }
         } else {
            img.Rgb = new (byte, byte, byte)[height, width];
            for (int y = 0; y < height; y++) {
               int rowOff = y * rowBytes;
               for (int x = 0; x < width; x++) {
                  int o = rowOff + x * channels;
                  byte r = raw[o], g = channels >= 3 ? raw[o + 1] : raw[o], b = channels >= 3 ? raw[o + 2] : raw[o];
                  img.Rgb[y, x] = (r, g, b);
               }
            }
         }
         return img;
      }

      private static int ReadSample(byte[] row, int rowOff, int x, int bitDepth) {
         if (bitDepth == 8) return row[rowOff + x];
         int perByte = 8 / bitDepth, mask = (1 << bitDepth) - 1;
         byte b = row[rowOff + x / perByte];
         int shift = 8 - bitDepth - (x % perByte) * bitDepth;
         return (b >> shift) & mask;
      }

      // PNG scanline defiltering (filter types 0-4), per the spec.
      private static byte[] Unfilter(byte[] inflated, int height, int rowBytes, int bpp) {
         var outBytes = new byte[height * rowBytes];
         int src = 0;
         for (int y = 0; y < height; y++) {
            int filter = inflated[src++];
            int rowOff = y * rowBytes, prevRow = (y - 1) * rowBytes;
            for (int i = 0; i < rowBytes; i++) {
               int a = i >= bpp ? outBytes[rowOff + i - bpp] : 0;
               int b = y > 0 ? outBytes[prevRow + i] : 0;
               int c = (y > 0 && i >= bpp) ? outBytes[prevRow + i - bpp] : 0;
               int x = inflated[src++];
               int val = filter switch {
                  0 => x,
                  1 => x + a,
                  2 => x + b,
                  3 => x + (a + b) / 2,
                  4 => x + Paeth(a, b, c),
                  _ => throw new NotSupportedException($"Unsupported PNG filter {filter}."),
               };
               outBytes[rowOff + i] = (byte)(val & 0xFF);
            }
         }
         return outBytes;
      }
      private static int Paeth(int a, int b, int c) {
         int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
         return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
      }

      private static byte[] ZlibCompress(byte[] data) {
         using var ms = new MemoryStream();
         using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data, 0, data.Length);
         return ms.ToArray();
      }
      private static byte[] ZlibDecompress(byte[] data) {
         using var input = new MemoryStream(data);
         using var z = new ZLibStream(input, CompressionMode.Decompress);
         using var ms = new MemoryStream();
         z.CopyTo(ms);
         return ms.ToArray();
      }

      private static void WriteChunk(Stream s, string type, byte[] data) {
         var lenBuf = new byte[4]; WriteBE(lenBuf, 0, (uint)data.Length); s.Write(lenBuf, 0, 4);
         var typeAndData = new byte[4 + data.Length];
         for (int i = 0; i < 4; i++) typeAndData[i] = (byte)type[i];
         Array.Copy(data, 0, typeAndData, 4, data.Length);
         s.Write(typeAndData, 0, typeAndData.Length);
         var crcBuf = new byte[4]; WriteBE(crcBuf, 0, Crc(typeAndData)); s.Write(crcBuf, 0, 4);
      }
      private static void WriteBE(byte[] buf, int off, uint v) { buf[off] = (byte)(v >> 24); buf[off + 1] = (byte)(v >> 16); buf[off + 2] = (byte)(v >> 8); buf[off + 3] = (byte)v; }
      private static uint ReadBE(byte[] buf, int off) => ((uint)buf[off] << 24) | ((uint)buf[off + 1] << 16) | ((uint)buf[off + 2] << 8) | buf[off + 3];
   }
}
