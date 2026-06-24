using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs.Sprites {
   /// <summary>
   /// A table where each row IS its own uncompressed sprite directly, with no pointer
   /// indirection - e.g. Format = '[`ucs4x3x3|some.palette.table`]duelists'. Mirrors
   /// OverworldSpriteListRun's ITableRun+ISpriteRun hybrid pattern, reading each row's bytes
   /// directly rather than through a pointer. ISpriteRun's "page" doubles as the row index, so
   /// the existing Sprite Tool page-stepping UI (Next/Previous Sprite Page) is what lets a user
   /// click through every row's sprite - no new UI plumbing needed.
   /// </summary>
   public class InlineSpriteTableRun : BaseRun, ITableRun, ISpriteRun {
      private readonly IDataModel model;

      public SpriteFormat SpriteFormat { get; }
      public int Pages => ElementCount;
      public override int Length { get; }
      public override string FormatString { get; }

      public bool SupportsImport => true;
      public bool SupportsEdit => true;
      public int DecompressedLength => Length;

      public int ElementCount { get; }
      public int ElementLength { get; }
      public IReadOnlyList<string> ElementNames { get; }
      public IReadOnlyList<ArrayRunElementSegment> ElementContent { get; }
      public bool CanAppend => false;

      public InlineSpriteTableRun(IDataModel model, SpriteFormat format, int elementCount, IReadOnlyList<string> elementNames, int start, SortedSpan<int> sources, string formatString) : base(start, sources) {
         this.model = model;
         SpriteFormat = format;
         ElementCount = Math.Max(1, elementCount);
         ElementLength = new SpriteRun(model, 0, format).Length;
         Length = ElementLength * ElementCount;
         ElementNames = elementNames;
         FormatString = formatString;
         ElementContent = new ArrayRunElementSegment[] { new InlineImageElementSegment(formatString, ElementLength) };
      }

      private int Wrap(int page) => ((page % ElementCount) + ElementCount) % ElementCount;
      private SpriteRun RowRun(int page) => new SpriteRun(model, Start + Wrap(page) * ElementLength, SpriteFormat);

      public override IDataFormat CreateDataFormat(IDataModel data, int index) {
         var rowIndex = (index - Start) / ElementLength;
         return RowRun(rowIndex).CreateDataFormat(data, index);
      }

      protected override BaseRun Clone(SortedSpan<int> newPointerSources) =>
         new InlineSpriteTableRun(model, SpriteFormat, ElementCount, ElementNames, Start, newPointerSources, FormatString);

      public ISpriteRun Duplicate(SpriteFormat newFormat) =>
         new InlineSpriteTableRun(model, newFormat, ElementCount, ElementNames, Start, PointerSources, FormatString);

      public byte[] GetData() => RowRun(0).GetData();

      public int[,] GetPixels(IDataModel model, int page, int tableIndex) => RowRun(page).GetPixels(model, 0, -1);

      public ISpriteRun SetPixels(IDataModel model, ModelDelta token, int page, int[,] pixels) {
         RowRun(page).SetPixels(model, token, 0, pixels);
         return new InlineSpriteTableRun(model, SpriteFormat, ElementCount, ElementNames, Start, PointerSources, FormatString);
      }

      public ISpriteRun SetPixels(IDataModel model, ModelDelta token, IReadOnlyList<int[,]> tiles) => throw new NotSupportedException("InlineSpriteTableRun rows are independent sprites, not tiles of one larger image.");

      public ITableRun Append(ModelDelta token, int length) => throw new NotImplementedException();
      public ITableRun Duplicate(int start, SortedSpan<int> pointerSources, IReadOnlyList<ArrayRunElementSegment> segments) => throw new NotImplementedException();
      public void AppendTo(IDataModel model, StringBuilder builder, int start, int length, int depth) => ITableRunExtensions.AppendTo(this, model, builder, start, length, depth);
      public void Clear(IDataModel model, ModelDelta changeToken, int start, int length) => ITableRunExtensions.Clear(this, model, changeToken, start, length);
   }
}
