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
using HavenSoft.HexManiac.Core.Models.Runs;
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
            case "backup_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var file = vp.FullFileName;
               if (string.IsNullOrEmpty(file)) return new AutoResponse(false, null, "This tab has no on-disk file to back up.");
               var stamp = System.DateTime.Now;
               var files = RomBackup.Create(file, stamp);
               if (files.Count == 0) return new AutoResponse(false, null, $"Nothing to back up (file not found: {file}).");
               return Ok(new { ok = true, timestamp = stamp.ToString("yyyyMMdd-HHmmss"), files });
            }
            case "save_rom": {
               var vp = ResolveTab(p);
               if (vp == null) return NoTab();
               var outPath = StrOrNull(p, "outPath");
               IReadOnlyList<string> Backup(string target) =>
                  !string.IsNullOrEmpty(target) && System.IO.File.Exists(target) ? RomBackup.Create(target, System.DateTime.Now) : System.Array.Empty<string>();
               if (!string.IsNullOrEmpty(outPath)) {
                  var backed = Backup(outPath);
                  System.IO.File.WriteAllBytes(outPath, vp.Model.RawData);
                  return Ok(new { ok = true, path = outPath, overwrote = false, length = vp.Model.RawData.Length, backedUp = backed });
               }
               if (!Bool(p, "overwrite", false))
                  return new AutoResponse(false, null, "Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source.");
               var backed2 = Backup(vp.FullFileName);
               vp.Save.Execute(GuiFileSystem());
               return Ok(new { ok = true, saved = vp.FullFileName ?? vp.Name, overwrote = true, length = vp.Model.RawData.Length, backedUp = backed2 });
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
            case "copy_rows": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               return Ok(RomAutomation.CopyRows(vp.Model, Str(p, "table"), Int(p, "index", -1), Int(p, "count", 1)));
            }
            case "paste_rows": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               return Ok(RomAutomation.PasteRows(vp.Model, () => vp.CurrentChange, Str(p, "table"), Int(p, "index", -1), Str(p, "data")));
            }
            case "select": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var t = vp.Model.GetTableModel(Str(p, "table"));
               if (t == null) return new AutoResponse(false, null, $"No table named '{Str(p, "table")}'.");
               int index = Int(p, "index", 0), count = Int(p, "count", 1);
               // index omitted (null) => whole table
               if (p.ValueKind == JsonValueKind.Object && (!p.TryGetProperty("index", out var iv) || iv.ValueKind == JsonValueKind.Null)) { index = 0; count = t.Count; }
               if (index < 0 || count < 1 || index + count > t.Count) return new AutoResponse(false, null, $"rows {index}..{index + count - 1} out of range (0..{t.Count - 1}).");
               int start = t[index].Start, last = t[index + count - 1].Start + t[index + count - 1].Length - 1;
               vp.SelectionStart = vp.ConvertAddressToViewPoint(start);
               vp.SelectionEnd = vp.ConvertAddressToViewPoint(last);
               return Ok(new { ok = true, table = Str(p, "table"), index, count, start, length = last - start + 1 });
            }
            case "clipboard_copy": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var fs = GuiFileSystem();
               vp.Copy.Execute(fs);
               return Ok(new { ok = true, text = fs.CopyText ?? "" });
            }
            case "clipboard_paste": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var text = GuiFileSystem().CopyText ?? "";
               if (text.Length > 0) { vp.Edit(text); vp.ChangeHistory.ChangeCompleted(); }
               return Ok(new { ok = true, pasted = text.Length });
            }
            case "open_rom": {
               var path = Str(p, "path");
               if (string.IsNullOrEmpty(path)) return new AutoResponse(false, null, "Provide an absolute 'path' to a .gba file.");
               var full = System.IO.Path.GetFullPath(path);
               if (!System.IO.File.Exists(full)) return new AutoResponse(false, null, $"File not found: {full}");
               var loaded = GuiFileSystem().LoadFile(full);
               if (loaded == null) return new AutoResponse(false, null, $"Could not load: {full}");
               var vp = editor.OpenFileAsTab(loaded);
               if (vp == null) return new AutoResponse(false, null, editor.ErrorMessage is { Length: > 0 } e ? e : "Open failed.");
               int index = -1, i = 0;
               foreach (var t in editor) { if (ReferenceEquals(t, vp)) { index = i; break; } i++; }
               return Ok(new { ok = true, path = full, index, file = vp.FullFileName ?? vp.Name });
            }
            case "close_tab": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               bool force = Bool(p, "force", false);
               if (vp.ChangeHistory.HasDataChange && !force)
                  return new AutoResponse(false, null, $"Tab '{vp.FullFileName ?? vp.Name}' has unsaved changes; pass force=true to discard.");
               var file = vp.FullFileName ?? vp.Name;
               CloseTabNoPrompt(vp);
               return Ok(new { ok = true, closed = file, remaining = CountTabs() });
            }
            case "close_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               bool force = Bool(p, "force", false);
               var group = new List<ViewPort>();
               foreach (var t in editor) if (t is ViewPort v && ReferenceEquals(v.Model, vp.Model)) group.Add(v);
               if (!force) {
                  var dirty = group.FirstOrDefault(v => v.ChangeHistory.HasDataChange);
                  if (dirty != null) return new AutoResponse(false, null, $"ROM has unsaved changes in tab '{dirty.FullFileName ?? dirty.Name}'; pass force=true to discard.");
               }
               foreach (var v in group) CloseTabNoPrompt(v);
               return Ok(new { ok = true, closedCount = group.Count, remaining = CountTabs() });
            }
            case "duplicate_tab": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               if (!vp.CanDuplicate) return new AutoResponse(false, null, $"Tab '{vp.FullFileName ?? vp.Name}' cannot be duplicated.");
               var child = vp.CreateDuplicate();
               editor.Add(child);
               int index = -1, i = 0;
               foreach (var t in editor) { if (ReferenceEquals(t, child)) { index = i; break; } i++; }
               return Ok(new { ok = true, index, file = child.FullFileName ?? child.Name });
            }
            case "launch_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var file = vp.FullFileName;
               if (string.IsNullOrEmpty(file) || !System.IO.File.Exists(file))
                  return new AutoResponse(false, null, "This tab has no saved on-disk ROM to launch.");
               if (vp.ChangeHistory.HasDataChange && !Bool(p, "force", false))
                  return new AutoResponse(false, null, "ROM has unsaved changes; save first (save_rom), or pass force=true to launch the last-saved file on disk.");
               var full = System.IO.Path.GetFullPath(file);
               GuiFileSystem().LaunchProcess(full);
               return Ok(new { ok = true, launched = full });
            }
            case "identify_rom": {
               var vp = ResolveTab(p); if (vp == null) return NoTab();
               var bytes = vp.Model.RawData is byte[] b ? b : System.Linq.Enumerable.ToArray(vp.Model.RawData);
               var code = vp.Model.GetGameCode();
               var md5 = string.Concat(System.Security.Cryptography.MD5.HashData(bytes).Select(x => x.ToString("x2")));
               var sha1 = string.Concat(System.Security.Cryptography.SHA1.HashData(bytes).Select(x => x.ToString("X2")));
               var crc32 = Force.Crc32.Crc32Algorithm.Compute(bytes).ToString("X8");
               return Ok(IdentifyMatch(code, md5, sha1, crc32));
            }
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

      // Identify a ROM by its hashes against the embedded supported-roms.json.
      private static object IdentifyMatch(string gameCode, string md5, string sha1, string crc32) {
         var asm = System.Reflection.Assembly.GetExecutingAssembly();
         var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("supported-roms.json", StringComparison.OrdinalIgnoreCase));
         string json = name != null
            ? new System.IO.StreamReader(asm.GetManifestResourceStream(name)).ReadToEnd()
            : "{}";
         using var doc = System.Text.Json.JsonDocument.Parse(json);
         System.Text.Json.JsonElement? byCode = null, byMd5 = null;
         foreach (var section in new[] { "roms", "notSupported", "alsoSupported" }) {
            if (!doc.RootElement.TryGetProperty(section, out var arr)) continue;
            foreach (var e in arr.EnumerateArray()) {
               if (e.TryGetProperty("headerCode", out var c) && string.Equals(c.GetString(), gameCode, StringComparison.OrdinalIgnoreCase)) byCode = e.Clone();
               if (e.TryGetProperty("md5", out var m) && string.Equals(m.GetString(), md5, StringComparison.OrdinalIgnoreCase)) byMd5 = e.Clone();
            }
         }
         string Get(System.Text.Json.JsonElement? el, string p) => el is System.Text.Json.JsonElement j && j.TryGetProperty(p, out var v) ? v.GetString() : null;
         var baseGame = Get(byCode, "game");
         var note = baseGame == null
            ? $"Unrecognized header code '{gameCode}'; HMA opens this as a plain hex editor."
            : (byMd5 != null ? "Clean No-Intro dump." : "Header matches a supported base, but the bytes don't match any known clean dump — edited / romhack.");
         return new {
            ok = true,
            headerCode = gameCode,
            baseGame,
            revision = Get(byCode, "revision"),
            hmaSupport = Get(byCode, "hmaSupport"),
            isCleanDump = byMd5 != null,
            matchedNoIntro = Get(byMd5, "noIntroName"),
            md5, sha1, crc32, note
         };
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

      // Close a tab without ever showing a save dialog: tag the history saved (so
      // CloseExecuted skips its TrySavePrompt) then run the tab's Close. Discards
      // unsaved edits (no disk write). Caller already enforced the force gate.
      private void CloseTabNoPrompt(ViewPort vp) {
         if (vp.ChangeHistory.HasDataChange) vp.ChangeHistory.TagAsSaved();
         vp.Close.Execute(GuiFileSystem());
      }

      private int CountTabs() {
         int n = 0;
         foreach (var t in editor) if (t is ViewPort) n++;
         return n;
      }

      private static bool Bool(JsonElement p, string key, bool fallback) {
         if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(key, out var v)) return fallback;
         return v.ValueKind switch {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : fallback,
            _ => fallback,
         };
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
