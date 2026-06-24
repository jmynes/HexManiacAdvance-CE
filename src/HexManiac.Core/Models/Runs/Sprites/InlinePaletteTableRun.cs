using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs.Sprites {
   /// <summary>
   /// A table where each row IS its own uncompressed palette directly, with no pointer
   /// indirection - e.g. Format = '[`ucp4`]duelists'. Mirrors OverworldSpriteListRun's
   /// ITableRun+ISpriteRun hybrid pattern, but for palettes, and reads each row's bytes
   /// directly rather than through a pointer. IPaletteRun's "page" doubles as the row index.
   /// </summary>
   public class InlinePaletteTableRun : BaseRun, ITableRun, IPaletteRun {
      public PaletteFormat PaletteFormat { get; }
      public int Pages => ElementCount;
      public override int Length { get; }
      public override string FormatString { get; }

      public int ElementCount { get; }
      public int ElementLength { get; }
      public IReadOnlyList<string> ElementNames { get; }
      public IReadOnlyList<ArrayRunElementSegment> ElementContent { get; }
      public bool CanAppend => false;

      public InlinePaletteTableRun(PaletteFormat format, int elementCount, IReadOnlyList<string> elementNames, int start, SortedSpan<int> sources, string formatString) : base(start, sources) {
         PaletteFormat = format;
         ElementCount = Math.Max(1, elementCount);
         ElementLength = new PaletteRun(0, format).Length;
         Length = ElementLength * ElementCount;
         ElementNames = elementNames;
         FormatString = formatString;
         ElementContent = new ArrayRunElementSegment[] { new InlineImageElementSegment(formatString, ElementLength) };
      }

      public override IDataFormat CreateDataFormat(IDataModel data, int index) {
         var rowIndex = (index - Start) / ElementLength;
         var row = new PaletteRun(Start + rowIndex * ElementLength, PaletteFormat);
         return row.CreateDataFormat(data, index);
      }

      protected override BaseRun Clone(SortedSpan<int> newPointerSources) =>
         new InlinePaletteTableRun(PaletteFormat, ElementCount, ElementNames, Start, newPointerSources, FormatString);

      public IPaletteRun Duplicate(PaletteFormat newFormat) =>
         new InlinePaletteTableRun(newFormat, ElementCount, ElementNames, Start, PointerSources, FormatString);

      private int Wrap(int page) => ((page % ElementCount) + ElementCount) % ElementCount;

      public IReadOnlyList<short> GetPalette(IDataModel model, int page) {
         var row = new PaletteRun(Start + Wrap(page) * ElementLength, PaletteFormat);
         return row.GetPalette(model, 0);
      }

      public IPaletteRun SetPalette(IDataModel model, ModelDelta token, int page, IReadOnlyList<short> colors) {
         var row = new PaletteRun(Start + Wrap(page) * ElementLength, PaletteFormat);
         row.SetPalette(model, token, 0, colors);
         return new InlinePaletteTableRun(PaletteFormat, ElementCount, ElementNames, Start, PointerSources, FormatString);
      }

      public ITableRun Append(ModelDelta token, int length) => throw new NotImplementedException();
      public ITableRun Duplicate(int start, SortedSpan<int> pointerSources, IReadOnlyList<ArrayRunElementSegment> segments) => throw new NotImplementedException();
      public void AppendTo(IDataModel model, StringBuilder builder, int start, int length, int depth) => ITableRunExtensions.AppendTo(this, model, builder, start, length, depth);
      public void Clear(IDataModel model, ModelDelta changeToken, int start, int length) => ITableRunExtensions.Clear(this, model, changeToken, start, length);
   }
}
