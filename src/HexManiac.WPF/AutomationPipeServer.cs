using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;

namespace HavenSoft.HexManiac.WPF.Windows {
   // Named-pipe automation server hosted inside the GUI. Lets the MCP server
   // (a separate process) operate on the ROMs currently open in this editor.
   //
   // Every request is marshaled onto the WPF UI thread before touching a tab,
   // because the model/view-model are not thread-safe.
   public class AutomationPipeServer {
      public const string PipeName = "HexManiacAdvance.Automation";

      private readonly EditorViewModel editor;

      public AutomationPipeServer(EditorViewModel editor) => this.editor = editor;

      public void Start() =>
         new Thread(Loop) { IsBackground = true, Name = "MCP-Automation" }.Start();

      private void Loop() {
         while (true) {
            try {
               using var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                  PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
               server.WaitForConnection();
               using var reader = new StreamReader(server, Encoding.UTF8, false, 1024, leaveOpen: true);
               using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
               string line;
               while ((line = reader.ReadLine()) != null) {
                  AutoResponse resp;
                  try {
                     var req = JsonSerializer.Deserialize<AutoRequest>(line);
                     resp = Application.Current.Dispatcher.Invoke(() => Handle(req));
                  } catch (Exception ex) {
                     resp = new AutoResponse(false, null, ex.Message);
                  }
                  writer.WriteLine(JsonSerializer.Serialize(resp));
               }
            } catch {
               // client dropped or pipe error: loop and re-listen
            }
         }
      }

      // ---- request handling (runs on the UI thread) ----

      private AutoResponse Handle(AutoRequest req) {
         var p = req.Params;
         switch (req.Method) {
            case "list_tabs":
               return Ok(new { tabs = ListTabs() });
            case "list_tables": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               return Ok(RomAutomation.ListTables(vp.Model, StrOrNull(p, "filter")));
            }
            case "read_table": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               return Ok(RomAutomation.ReadTable(vp.Model, Str(p, "name"), Int(p, "start", 0), Int(p, "count", 25)));
            }
            case "write_value": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               // Edit through the tab's change token so it enters GUI undo history
               // and renders immediately.
               var table = Str(p, "table");
               var index = Int(p, "index", -1);
               var result = RomAutomation.WriteValue(vp.Model, () => vp.CurrentChange,
                  table, index, Str(p, "field"), Val(p, "value"), StrOrNull(p, "flag"));
               // On success, focus the edited row in the GUI so the change is visible.
               if (result is System.Collections.IDictionary d && !d.Contains("error"))
                  vp.Goto.Execute($"{table}/{index}");
               return Ok(result);
            }
            case "export_table": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               return Ok(RomAutomation.ExportToFile(vp.Model, Str(p, "name"), Str(p, "outPath")));
            }
            case "run_script": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               var errs = new List<string>();
               void OnErr(object s, string e) => errs.Add(e);
               vp.OnError += OnErr;
               try {
                  var path = StrOrNull(p, "path");
                  if (!string.IsNullOrEmpty(path)) {
                     var full = Path.GetFullPath(path);
                     if (!File.Exists(full)) return new AutoResponse(false, null, $"Script file not found: {full}");
                     vp.TryImport(new LoadedFile(full, File.ReadAllBytes(full)), GuiFileSystem());
                  } else {
                     var script = Str(p, "script");
                     if (string.IsNullOrEmpty(script)) return new AutoResponse(false, null, "Provide 'script' text or 'path'.");
                     vp.Edit(script);
                  }
                  vp.ChangeHistory.ChangeCompleted();
               } finally {
                  vp.OnError -= OnErr;
               }
               return Ok(new { ok = errs.Count == 0, errors = errs, ranOn = vp.FullFileName ?? vp.Name });
            }
            case "save_rom": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               vp.Save.Execute(GuiFileSystem());
               return Ok(new { ok = true, saved = vp.FullFileName ?? vp.Name });
            }
            case "list_shortcuts": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               return Ok(RomAutomation.ListShortcuts(vp.Model));
            }
            case "goto": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               var target = Str(p, "target");
               if (string.IsNullOrEmpty(target))
                  return new AutoResponse(false, null, "Provide a 'target' (shortcut label, anchor, or address).");
               if (!TryResolveGotoTarget(vp.Model, target, out var resolved))
                  return new AutoResponse(false, null, $"Unknown goto target '{target}'. Use list_shortcuts, or a valid anchor/address.");
               editor.GotoAndCloseShortcutsPanel(vp, resolved);
               return Ok(new { ok = true, target, resolved, tab = vp.FullFileName ?? vp.Name });
            }
            case "undo": return Ok(ApplyHistory(ResolveTab(p), Int(p, "count", 1), redo: false));
            case "redo": return Ok(ApplyHistory(ResolveTab(p), Int(p, "count", 1), redo: true));
            default:
               return new AutoResponse(false, null, $"Unknown method: {req.Method}");
         }
      }

      private List<object> ListTabs() {
         var list = new List<object>();
         int i = 0;
         foreach (var t in editor) {
            if (t is ViewPort vp) {
               list.Add(new { index = i, file = vp.FullFileName ?? vp.Name, selected = ReferenceEquals(t, editor.SelectedTab) });
            }
            i++;
         }
         return list;
      }

      // Pick the target ROM tab: by "tab" index or filename substring, else the
      // selected tab, else the first ROM tab.
      private ViewPort ResolveTab(JsonElement p) {
         var tabs = new List<ViewPort>();
         foreach (var t in editor) if (t is ViewPort vp) tabs.Add(vp);
         if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("tab", out var tab) && tab.ValueKind != JsonValueKind.Null) {
            if (tab.ValueKind == JsonValueKind.Number) return tabs.ElementAtOrDefault(tab.GetInt32());
            if (tab.ValueKind == JsonValueKind.String) {
               var s = tab.GetString();
               return tabs.FirstOrDefault(v => (v.FullFileName ?? v.Name ?? "").Contains(s, StringComparison.OrdinalIgnoreCase));
            }
         }
         return editor.SelectedTab as ViewPort ?? tabs.FirstOrDefault();
      }

      // Resolve a goto target without mutating data. A shortcut DisplayText maps
      // to its GotoAnchor; otherwise the target must already be a known anchor or
      // a hex address (the inputs ViewPort.ExecuteGoto accepts), so we never
      // silently no-op on a bad target.
      private static bool TryResolveGotoTarget(IDataModel model, string target, out string resolved) {
         var shortcut = model.GotoShortcuts.FirstOrDefault(s =>
            string.Equals(s.DisplayText, target, StringComparison.OrdinalIgnoreCase));
         if (shortcut != null) { resolved = shortcut.GotoAnchor; return true; }
         resolved = target;
         if (model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, target) != Pointer.NULL) return true;
         if (target.TryParseHex(out _)) return true;
         return false;
      }

      private static AutoResponse Ok(object result) =>
         new AutoResponse(true, JsonSerializer.SerializeToElement(result), null);

      private static AutoResponse NoTab() =>
         new AutoResponse(false, null, "No open ROM tab in the GUI.");

      private static object ApplyHistory(ViewPort vp, int count, bool redo) {
         if (vp == null) return new { error = "No open ROM tab in the GUI." };
         vp.ChangeHistory.ChangeCompleted();            // commit any in-progress edit so it is on the stack
         var cmd = redo ? vp.Redo : vp.Undo;
         int applied = 0;
         while (applied < count && cmd.CanExecute(null)) { cmd.Execute(null); applied++; }
         return new { ok = true, applied };
      }

      private static IFileSystem GuiFileSystem() =>
         ((MainWindow)Application.Current.MainWindow).FileSystem;

      private static string Str(JsonElement p, string key, string fallback = "") =>
         p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : fallback;

      private static string StrOrNull(JsonElement p, string key) {
         var s = Str(p, key, null);
         return string.IsNullOrEmpty(s) ? null : s;
      }

      private static int Int(JsonElement p, string key, int fallback) =>
         p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : fallback;

      // Read a JSON scalar param as the CLR value the engine expects.
      private static object Val(JsonElement p, string key) {
         if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(key, out var v)) return 0;
         return v.ValueKind switch {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (object)v.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => v.ToString(),
         };
      }
   }
}
