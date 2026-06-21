using System.Collections.Generic;

namespace HavenSoft.HexManiac.Core.Models.Runs.Factory {
   /// <summary>
   /// Format Specifier: a field list with no enclosing [...]length, e.g. "padding:: count: [card:names]/count"
   /// Represents a single struct (not a repeating table) living at a pointer's destination - basic
   /// fields mixed with InlineArraySegment sections sized by a sibling field. See StructRun.
   /// </summary>
   public class StructRunContentStrategy : RunStrategy {
      public override int LengthForNewRun(IDataModel model, int pointerAddress) {
         if (!StructRun.TryParseFields(Format, model, out var specs)) return 0;
         return StructRun.ComputeMinimalLength(specs);
      }

      public override bool TryAddFormatAtDestination(IDataModel owner, ModelDelta token, int source, int destination, string name, IReadOnlyList<ArrayRunElementSegment> sourceSegments, int parentIndex) {
         if (!StructRun.TryParseFields(Format, owner, out var specs)) return false;
         try {
            var run = new StructRun(owner, destination, new SortedSpan<int>(source), Format, specs);
            if (token is not NoDataChangeDeltaModel) owner.ObserveRunWritten(token, run);
            return true;
         } catch (ArrayRunParseException) {
            return false;
         }
      }

      public override bool Matches(IFormattedRun run) => run is StructRun structRun && structRun.FormatString == Format;

      public override IFormattedRun WriteNewRun(IDataModel owner, ModelDelta token, int source, int destination, string name, IReadOnlyList<ArrayRunElementSegment> sourceSegments) {
         StructRun.TryParseFields(Format, owner, out var specs);
         var minimalLength = StructRun.ComputeMinimalLength(specs);
         for (int i = 0; i < minimalLength; i++) token.ChangeData(owner, destination + i, 0);
         return new StructRun(owner, destination, new SortedSpan<int>(source), Format, specs);
      }

      public override void UpdateNewRunFromPointerFormat(IDataModel model, ModelDelta token, string name, IReadOnlyList<ArrayRunElementSegment> sourceSegments, int parentIndex, ref IFormattedRun run) {
         if (!StructRun.TryParseFields(Format, model, out var specs)) return;
         try {
            run = new StructRun(model, run.Start, run.PointerSources, Format, specs);
         } catch (ArrayRunParseException) { }
      }

      public override ErrorInfo TryParseData(IDataModel model, string name, int dataIndex, ref IFormattedRun run) {
         if (!StructRun.TryParseFields(Format, model, out var specs)) return new ErrorInfo($"Format {Format} was not understood.");
         try {
            run = new StructRun(model, dataIndex, run.PointerSources, Format, specs);
            return ErrorInfo.NoError;
         } catch (ArrayRunParseException e) {
            return new ErrorInfo($"Format {Format} was not understood: " + e.Message);
         }
      }
   }
}
