using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using HavenSoft.HexManiac.Core.Models;
using ModelContextProtocol.Server;

namespace HavenSoft.HexManiac.Mcp;

// MCP tools for reading/editing GBA Pokémon ROM data via HexManiac.Core.
//
// The RomSession parameter is supplied by dependency injection (a registered
// singleton); the remaining parameters are the tool's arguments. Each tool
// returns a JSON string with a "mode" field ("headless" here; "live" once the
// GuiBridge is added). Shared table logic lives in HexManiac.Core/RomAutomation.
[McpServerToolType]
public sealed class RomTools {
   private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

   [McpServerTool(Name = "open_rom")]
   [Description("Open a GBA ROM. Live: new GUI tab; headless: single session. For ROMs that are NOT a recognized base game (FireRed/Emerald/...) AND have no sidecar .toml, HexManiac guesses table offsets; under metadata='auto' this returns a warning with choices instead of opening. metadata: 'auto' (default) | 'find_toml' | 'guess_offsets' | 'guess'.")]
   public string OpenRom(RomSession session,
      [Description("Absolute path to a .gba ROM file")] string path,
      [Description("auto | find_toml | guess_offsets | guess")] string metadata = "auto") {
      string mode = GuiBridge.IsGuiRunning() ? "live" : "headless";
      string err(string m) => Stamp(JsonSerializer.Serialize(RomAutomation.Err(m), Json), mode);
      if (!File.Exists(path)) return err($"ROM not found: {path}");
      string gameCode;
      try { gameCode = ((IReadOnlyList<byte>)File.ReadAllBytes(path)).GetGameCode(); }
      catch (System.Exception ex) { return err($"Could not read ROM header: {ex.Message}"); }
      bool recognized = session.Singletons.GameReferenceTables.TryGetValue(gameCode, out _);
      var tomlPath = Path.ChangeExtension(path, ".toml");
      bool hasToml = File.Exists(tomlPath);

      switch (metadata) {
         case "guess_offsets":
            return err("Auto-detecting table offsets for an unrecognized ROM is not yet implemented. Please bug jmynes on GitHub or jordank.memes on Discord for this feature.");
         case "find_toml":
            if (!hasToml) return err($"No sidecar .toml found next to '{path}' (looked for '{tomlPath}'). Use metadata='guess' to open with guessed offsets, or metadata='guess_offsets'.");
            return OpenProceed(session, path, gameCode, recognized, "toml", File.ReadAllLines(tomlPath), false);
         case "guess":
            return OpenProceed(session, path, gameCode, recognized, hasToml ? "toml" : (recognized ? "builtin" : "guessed"), hasToml ? File.ReadAllLines(tomlPath) : null, warnGuess: !recognized && !hasToml);
         case "auto":
         default:
            if (recognized || hasToml)
               return OpenProceed(session, path, gameCode, recognized, hasToml ? "toml" : "builtin", hasToml ? File.ReadAllLines(tomlPath) : null, false);
            var warn = new Dictionary<string, object?> {
               ["ok"] = false, ["needsMetadataChoice"] = true, ["gameCode"] = gameCode,
               ["recognized"] = false, ["hasToml"] = false,
               ["warning"] = $"'{path}' is not a recognized base game (e.g. FireRed/Emerald) and has no sidecar .toml, so HexManiac would guess table offsets — reads and writes may be inaccurate.",
               ["options"] = new Dictionary<string, object?> {
                  ["find_toml"] = "Re-call open_rom with metadata='find_toml' to use a .toml placed next to the ROM.",
                  ["guess_offsets"] = "Re-call with metadata='guess_offsets' to auto-detect offsets (not yet implemented).",
                  ["guess"] = "Re-call with metadata='guess' to open anyway with guessed metadata.",
               },
            };
            return Stamp(JsonSerializer.Serialize(warn, Json), mode);
      }
   }

   private static string OpenProceed(RomSession session, string path, string gameCode, bool recognized, string metadataSource, string[]? tomlLines, bool warnGuess) {
      var p = new Dictionary<string, object?> { ["path"] = path };
      var json = Dispatch("open_rom", p, null, null, () => {
         if (tomlLines != null) session.Load(path, tomlLines); else session.Load(path);
         var model = session.Require();
         return new { ok = true, path, length = model.Count, anchorCount = model.Anchors.Count };
      });
      var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
      node["gameCode"] = gameCode;
      node["recognized"] = recognized;
      node["metadataSource"] = metadataSource;
      if (warnGuess) node["warning"] = "Opened with guessed metadata — this ROM is not a recognized base game and has no sidecar .toml; offsets may be inaccurate.";
      return node.ToJsonString();
   }

   [McpServerTool(Name = "list_open_roms")]
   [Description("List the ROMs currently open: the running GUI's tabs if a GUI is up, otherwise the headless-loaded ROM (or empty).")]
   public string ListOpenRoms(RomSession session) {
      var result = GuiBridge.TryCall("list_tabs", new Dictionary<string, object?>(), out var json, out _);
      if (result == GuiCallResult.Ok)
         return Stamp(json, "live");
      // For both Error and Unreachable, fall back to the headless tab listing.
      // A list_tabs error from the GUI is not worth surfacing here.
      object open = session.Model != null
         ? new { tabs = new[] { new { index = 0, file = session.RomPath, selected = true } } }
         : new { tabs = System.Array.Empty<object>() };
      return Stamp(JsonSerializer.Serialize(open, Json), "headless");
   }

   [McpServerTool(Name = "list_shortcuts")]
   [Description("List the GUI 'Goto' shortcut buttons (e.g. Pokemon, Trainers) as {display, anchor}. Targets the GUI's active tab when live; else headless.")]
   public string ListShortcuts(
      RomSession session,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      return Dispatch("list_shortcuts", p, tab, tabFile, () => RomAutomation.ListShortcuts(session.Require()));
   }

   [McpServerTool(Name = "goto")]
   [Description("Navigate the live GUI to a target: a shortcut label (e.g. Pokemon), an anchor name (e.g. data.pokemon.stats), or a hex address. Live GUI only; headless returns an error.")]
   public string Goto(
      RomSession session,
      [Description("Shortcut label, anchor name, or hex address to navigate to")] string target,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["target"] = target };
      return Dispatch("goto", p, tab, tabFile,
         () => RomAutomation.Err("goto requires the live GUI (no view to navigate in headless mode)."));
   }

   [McpServerTool(Name = "list_tables")]
   [Description("List the named data tables (anchors) in the open ROM. Targets the GUI's active tab when live; optional substring filter and tab selector.")]
   public string ListTables(
      RomSession session,
      [Description("Optional substring to filter anchor names")] string? filter = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      if (!string.IsNullOrEmpty(filter)) p["filter"] = filter;
      return Dispatch("list_tables", p, tab, tabFile, () => RomAutomation.ListTables(session.Require(), filter));
   }

   [McpServerTool(Name = "read_table")]
   [Description("Read a named table as JSON rows. Targets the GUI's active tab when live. Use start/count to page; tab/tabFile to pick a tab.")]
   public string ReadTable(
      RomSession session,
      [Description("Anchor/table name, e.g. data.pokemon.stats")] string name,
      [Description("First row index to return")] int start = 0,
      [Description("Maximum rows to return")] int count = 25,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["name"] = name, ["start"] = start, ["count"] = count };
      return Dispatch("read_table", p, tab, tabFile, () => RomAutomation.ReadTable(session.Require(), name, start, count));
   }

   [McpServerTool(Name = "write_value")]
   [Description("Set a field on a table row. value is a string (text/enum name), number (integer/enum index), or true/false. For a bit-array checkbox, pass flag=\"<name>\" with value true/false. Live GUI when present (visible+undoable); else headless.")]
   public string WriteValue(
      RomSession session,
      [Description("Anchor/table name")] string table,
      [Description("Row index")] int index,
      [Description("Field name within the row")] string field,
      [Description("New value: string, number, or true/false (type decided by the field)")] JsonElement value,
      [Description("Optional: name of one checkbox within a bit-array field")] string? flag = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var v = JsonToValue(value);
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["field"] = field, ["value"] = v };
      if (!string.IsNullOrEmpty(flag)) p["flag"] = flag;
      return Dispatch("write_value", p, tab, tabFile,
         () => RomAutomation.WriteValue(session.Require(), () => session.Token, table, index, field, v, flag));
   }

   [McpServerTool(Name = "undo")]
   [Description("Undo up to 'count' steps on the active tab's change history (same stack as Ctrl+Z). One step reverts the whole uncommitted batch of edits since the last commit boundary (a save_rom, run_script, or prior undo/redo), not necessarily a single write_value. Live GUI when present; else headless.")]
   public string Undo(RomSession session, [Description("How many steps to undo")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["count"] = count };
      return Dispatch("undo", p, tab, tabFile, () => HistoryHeadless(session, count, redo: false));
   }

   [McpServerTool(Name = "redo")]
   [Description("Redo up to 'count' steps on the active tab's change history (same stack as Ctrl+Y); a step replays a whole previously-undone batch. Live GUI when present; else headless.")]
   public string Redo(RomSession session, [Description("How many steps to redo")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["count"] = count };
      return Dispatch("redo", p, tab, tabFile, () => HistoryHeadless(session, count, redo: true));
   }

   private static object HistoryHeadless(RomSession session, int count, bool redo) {
      var vp = session.RequireViewPort();
      vp.ChangeHistory.ChangeCompleted();
      var cmd = redo ? vp.Redo : vp.Undo;
      int applied = 0;
      while (applied < count && cmd.CanExecute(null)) { cmd.Execute(null); applied++; }
      return new { ok = true, applied };
   }

   // Convert a JSON scalar argument to the CLR value the engine expects.
   private static object? JsonToValue(JsonElement v) => v.ValueKind switch {
      JsonValueKind.String => v.GetString(),
      JsonValueKind.Number => v.TryGetInt32(out var i) ? (object?)i : v.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => v.ToString(),
   };

   private static string _lastCopiedHex = "";

   [McpServerTool(Name = "copy_rows")]
   [Description("Copy 'count' table rows starting at 'index' as a hex byte string (cached for paste_rows). Live GUI when present; else headless.")]
   public string CopyRows(RomSession session, [Description("Anchor/table name")] string table,
      [Description("First row index")] int index, [Description("How many rows")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["count"] = count };
      var json = Dispatch("copy_rows", p, tab, tabFile, () => RomAutomation.CopyRows(session.Require(), table, index, count));
      try { var hex = System.Text.Json.Nodes.JsonNode.Parse(json)?["bytes"]?.GetValue<string>(); if (!string.IsNullOrEmpty(hex)) _lastCopiedHex = hex; } catch { }
      return json;
   }

   [McpServerTool(Name = "paste_rows")]
   [Description("Paste row bytes onto the table starting at 'index' (undoable). 'data' is hex; if omitted, uses the last copy_rows result. Live GUI when present; else headless.")]
   public string PasteRows(RomSession session, [Description("Anchor/table name")] string table,
      [Description("Destination row index")] int index, [Description("Hex bytes to paste (default: last copy_rows)")] string? data = null,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var hex = string.IsNullOrEmpty(data) ? _lastCopiedHex : data;
      var p = new Dictionary<string, object?> { ["table"] = table, ["index"] = index, ["data"] = hex };
      return Dispatch("paste_rows", p, tab, tabFile, () => RomAutomation.PasteRows(session.Require(), () => session.Token, table, index, hex));
   }

   [McpServerTool(Name = "close_tab")]
   [Description("Close one tab in the live GUI (default: active tab). Refuses if the tab has unsaved changes unless force=true (which discards them). Live GUI only.")]
   public string CloseTab(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Discard unsaved changes")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("close_tab", p, tab, tabFile, () => RomAutomation.Err("close_tab requires the live GUI."));
   }

   [McpServerTool(Name = "close_rom")]
   [Description("Close ALL tabs showing the resolved tab's ROM in the live GUI. Refuses if any of those tabs has unsaved changes unless force=true (which discards them). Live GUI only.")]
   public string CloseRom(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null,
      [Description("Discard unsaved changes")] bool force = false) {
      var p = new Dictionary<string, object?> { ["force"] = force };
      return Dispatch("close_rom", p, tab, tabFile, () => RomAutomation.Err("close_rom requires the live GUI."));
   }

   [McpServerTool(Name = "duplicate_tab")]
   [Description("Open a second tab on the same ROM as the resolved tab (like Ctrl+T) — shares the ROM's model and undo history; close_rom then closes all such tabs. Live GUI only.")]
   public string DuplicateTab(RomSession session, [Description("Target GUI tab by index")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("duplicate_tab", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("duplicate_tab requires the live GUI."));
   }

   [McpServerTool(Name = "clipboard_copy")]
   [Description("Copy the live GUI's current selection to the system clipboard; returns the copied text. Live GUI only.")]
   public string ClipboardCopy(RomSession session, [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("clipboard_copy", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("clipboard_copy requires the live GUI."));
   }

   [McpServerTool(Name = "clipboard_paste")]
   [Description("Paste the system clipboard at the live GUI's current selection (undoable). Live GUI only.")]
   public string ClipboardPaste(RomSession session, [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      return Dispatch("clipboard_paste", new Dictionary<string, object?>(), tab, tabFile, () => RomAutomation.Err("clipboard_paste requires the live GUI."));
   }

   [McpServerTool(Name = "select")]
   [Description("Select rows in the live GUI: 'count' rows from 'index', or the whole table if 'index' is omitted. Live GUI only.")]
   public string Select(RomSession session, [Description("Anchor/table name")] string table,
      [Description("First row index (omit for the whole table)")] int? index = null, [Description("How many rows")] int count = 1,
      [Description("Target GUI tab by index")] int? tab = null, [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["table"] = table, ["count"] = count };
      if (index.HasValue) p["index"] = index.Value;
      return Dispatch("select", p, tab, tabFile, () => RomAutomation.Err("select requires the live GUI (no view to select in headless mode)."));
   }

   [McpServerTool(Name = "export_table")]
   [Description("Export an entire table (all rows, no paging) to a JSON file on disk. Targets the GUI's active tab when live; else headless.")]
   public string ExportTable(
      RomSession session,
      [Description("Anchor/table name to export")] string name,
      [Description("Absolute path of the .json file to write")] string outPath,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["name"] = name, ["outPath"] = outPath };
      return Dispatch("export_table", p, tab, tabFile,
         () => RomAutomation.ExportToFile(session.Require(), name, outPath));
   }

   [McpServerTool(Name = "run_script")]
   [Description("Run an HMA script. Provide inline 'script' text OR 'path' to a .hma file. Targets the GUI's active tab when live; else headless.")]
   public string RunScript(
      RomSession session,
      [Description("Inline HMA script text")] string script = "",
      [Description("Path to a .hma script file (takes precedence over inline text)")] string? path = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      if (!string.IsNullOrEmpty(script)) p["script"] = script;
      if (!string.IsNullOrEmpty(path)) p["path"] = path;
      return Dispatch("run_script", p, tab, tabFile, () => RunScriptHeadless(session, script, path));
   }

   private static object RunScriptHeadless(RomSession session, string script, string? path) {
      var vp = session.RequireViewPort();
      session.Errors.Clear();
      session.Messages.Clear();
      if (!string.IsNullOrEmpty(path)) {
         var full = Path.GetFullPath(path);
         if (!File.Exists(full)) return RomAutomation.Err($"Script file not found: {full}");
         vp.TryImport(new LoadedFile(full, File.ReadAllBytes(full)), session.FileSystem);
      } else if (!string.IsNullOrEmpty(script)) {
         vp.Edit(script);
      } else {
         return RomAutomation.Err("Provide 'script' text or 'path' to a .hma file.");
      }
      vp.ChangeHistory.ChangeCompleted();
      var errors = session.Errors.ToList();
      return new { ok = errors.Count == 0, errors, messages = session.Messages.ToList() };
   }

   [McpServerTool(Name = "save_rom")]
   [Description("Write the ROM to disk. Pass outPath to save a COPY there; pass overwrite=true (no outPath) to save over the loaded/open ROM. With neither, it refuses (won't silently overwrite the source). Live targets the resolved tab; else headless.")]
   public string SaveRom(
      RomSession session,
      [Description("Output path to save a COPY to")] string? outPath = null,
      [Description("Save over the loaded/open ROM in place (only used when outPath is omitted)")] bool overwrite = false,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?> { ["overwrite"] = overwrite };
      if (!string.IsNullOrEmpty(outPath)) p["outPath"] = outPath;
      return Dispatch("save_rom", p, tab, tabFile, () => SaveRomHeadless(session, outPath, overwrite));
   }

   private static object SaveRomHeadless(RomSession session, string? outPath, bool overwrite) {
      var model = session.Require();
      session.RequireViewPort().ChangeHistory.ChangeCompleted();
      if (!string.IsNullOrEmpty(outPath)) {
         File.WriteAllBytes(outPath, model.RawData);
         return new { ok = true, path = outPath, overwrote = false, length = model.RawData.Length };
      }
      if (!overwrite)
         return RomAutomation.Err("Refusing to overwrite the loaded ROM in place. Pass outPath to save a copy, or overwrite=true to save over the source.");
      if (string.IsNullOrEmpty(session.RomPath)) return RomAutomation.Err("No loaded ROM path to overwrite.");
      File.WriteAllBytes(session.RomPath, model.RawData);
      return new { ok = true, path = session.RomPath, overwrote = true, length = model.RawData.Length };
   }

   // ---- helpers ----

   // Prefer the live GUI (if reachable) for `method`; fall back to a headless
   // computation only when the GUI is truly unreachable. A live error response
   // is surfaced directly (mode:"live") rather than discarded.
   private static string Dispatch(string method, Dictionary<string, object?> liveParams, int? tab, string? tabFile, System.Func<object> headless) {
      if (tab.HasValue) liveParams["tab"] = tab.Value;
      else if (!string.IsNullOrEmpty(tabFile)) liveParams["tab"] = tabFile;
      var result = GuiBridge.TryCall(method, liveParams, out var json, out var gerror);
      return result switch {
         GuiCallResult.Ok          => Stamp(json, "live"),
         GuiCallResult.Error       => Stamp(JsonSerializer.Serialize(RomAutomation.Err(gerror), Json), "live"),
         _  /* Unreachable */      => Stamp(JsonSerializer.Serialize(headless(), Json), "headless"),
      };
   }

   // Serialize a result object and stamp mode:"headless".
   private static string Headless(object result) => Stamp(JsonSerializer.Serialize(result, Json), "headless");

   // Parse a JSON object string and add/overwrite a "mode" field.
   internal static string Stamp(string json, string mode) {
      var node = JsonNode.Parse(json)!.AsObject();
      node["mode"] = mode;
      return node.ToJsonString();
   }
}
