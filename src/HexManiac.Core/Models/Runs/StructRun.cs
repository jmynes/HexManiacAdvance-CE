using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// A struct living at a pointer's destination: an ordered field list, like a single row of an
   /// ArrayRun, but able to contain InlineArraySegment sections whose repeat count is read from an
   /// earlier sibling field (by name) rather than known from the format string alone. ElementCount
   /// is always 1 - this represents one struct's fields, not a repeating table.
   /// </summary>
   public class StructRun : BaseRun, ITableRun {
      private readonly IDataModel model;

      public IReadOnlyList<ArrayRunElementSegment> RawFieldSpecs { get; }
      public IReadOnlyList<ArrayRunElementSegment> ElementContent { get; }
      public override int Length { get; }
      public override string FormatString { get; }

      public int ElementCount => 1;
      public int ElementLength => Length;
      public IReadOnlyList<string> ElementNames { get; } = new[] { string.Empty };
      public bool CanAppend => false;

      public static bool TryParseFields(string content, IDataModel model, out List<ArrayRunElementSegment> fields) {
         fields = null;
         if (string.IsNullOrWhiteSpace(content)) return false;
         try {
            var parsed = ArrayRun.ParseSegments(content, model);
            if (parsed.Count == 0) return false;
            fields = parsed;
            return true;
         } catch (ArrayRunParseException) {
            return false;
         } catch (Exception) {
            // model may be null here (this can run as a cheap syntactic gate check before a
            // real model is available) - any field type that dereferences it (e.g. a nested
            // pointer<...> segment touching model.FormatRunFactory) just means "not a struct".
            return false;
         }
      }

      public static int ComputeMinimalLength(IReadOnlyList<ArrayRunElementSegment> specs) =>
         specs.Sum(spec => spec is InlineArraySegment ? 0 : spec.Length);

      public StructRun(IDataModel model, int start, SortedSpan<int> sources, string formatString, IReadOnlyList<ArrayRunElementSegment> rawFieldSpecs) : base(start, sources) {
         this.model = model;
         FormatString = formatString;
         RawFieldSpecs = rawFieldSpecs;
         ElementContent = Expand(model, start, rawFieldSpecs);
         Length = ElementContent.Sum(seg => seg.Length);
         if (Length == 0) throw new ArrayRunParseException("Struct Content Length must not be zero.");
      }

      // resolves each InlineArraySegment by finding its count field among the segments already
      // placed (so the count field must precede its array - there's no other way to know where
      // the array's bytes end without already knowing how many of them there are).
      private static List<ArrayRunElementSegment> Expand(IDataModel model, int start, IReadOnlyList<ArrayRunElementSegment> specs) {
         var result = new List<ArrayRunElementSegment>();
         foreach (var spec in specs) {
            if (spec is InlineArraySegment arraySpec) {
               var countIndex = result.FindIndex(s => s.Name == arraySpec.CountFieldName);
               if (countIndex == -1) throw new ArrayRunParseException($"Inline array '{arraySpec.Name}' must be preceded by its count field '{arraySpec.CountFieldName}'.");
               var countOffset = result.Take(countIndex).Sum(s => s.Length);
               var count = model.ReadMultiByteValue(start + countOffset, result[countIndex].Length);
               for (int i = 0; i < count; i++) result.Add(arraySpec.Template);
            } else {
               result.Add(spec);
            }
         }
         return result;
      }

      public override IDataFormat CreateDataFormat(IDataModel data, int index) => this.CreateSegmentDataFormat(data, index);

      protected override BaseRun Clone(SortedSpan<int> newPointerSources) => new StructRun(model, Start, newPointerSources, FormatString, RawFieldSpecs);

      public ITableRun Append(ModelDelta token, int length) => throw new NotImplementedException();

      public ITableRun Duplicate(int start, SortedSpan<int> pointerSources, IReadOnlyList<ArrayRunElementSegment> segments) => throw new NotImplementedException();

      public void AppendTo(IDataModel model, StringBuilder builder, int start, int length, int depth) => ITableRunExtensions.AppendTo(this, model, builder, start, length, depth);

      public void Clear(IDataModel model, ModelDelta changeToken, int start, int length) => ITableRunExtensions.Clear(this, model, changeToken, start, length);
   }
}
