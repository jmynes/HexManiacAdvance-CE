using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models {
   // Read-only introspection over the Python object model, shared by the MCP-headless
   // python_introspect tool and the WPF automation pipe. The "evaluate an arbitrary
   // expression" branch lives on PythonTool (it needs the live scope); this covers the
   // deterministic, model-only cases (namespaces and table schemas).
   public static class PythonIntrospection {
      // No target: the top-level Python namespace - anchor groups (data, scripts, ...) plus
      // the special globals a script can use.
      public static object Namespaces(IDataModel model) {
         var groups = model.Anchors.Select(a => a.Split('.')[0])
            .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
         return new Dictionary<string, object?> {
            ["ok"] = true,
            ["kind"] = "namespace",
            ["namespaces"] = groups,
            ["globals"] = new[] { "editor", "table", "model" },
            ["hint"] = "python_introspect <anchor> => a table's field schema; python_introspect <expression> => dir()+value; run_python runs code (print(row) dumps a row's fields).",
         };
      }

      // The table run for a named table anchor (e.g. "data.pokemon.stats"), or null if the
      // name isn't a table anchor (caller then falls back to expression introspection).
      public static ITableRun ResolveTable(IDataModel model, string target) {
         var address = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, target);
         if (address < 0 || address == Pointer.NULL) return null;
         return model.GetNextRun(address) as ITableRun;
      }

      // Field schema (name/type/length, + enum source) plus a ToString() dump of row 0.
      public static object TableSchema(IDataModel model, ITableRun run, string target) {
         var fields = new List<object>();
         foreach (var seg in run.ElementContent) {
            var type =
               seg is ArrayRunEnumSegment ? "Enum" :
               seg is ArrayRunBitArraySegment ? "BitArray" :
               seg is ArrayRunTupleSegment ? "Tuple" :
               seg.Type.ToString();
            var field = new Dictionary<string, object?> {
               ["name"] = seg.Name, ["type"] = type, ["length"] = seg.Length,
            };
            if (seg is ArrayRunEnumSegment en) field["enumSource"] = en.EnumName;
            fields.Add(field);
         }
         string sample = run.ElementCount > 0
            ? new ModelArrayElement(model, run.Start, 0, () => new NoDataChangeDeltaModel(), run).ToString()
            : null;
         return new Dictionary<string, object?> {
            ["ok"] = true, ["kind"] = "table", ["target"] = target,
            ["count"] = run.ElementCount, ["fields"] = fields, ["sample"] = sample,
         };
      }
   }
}
