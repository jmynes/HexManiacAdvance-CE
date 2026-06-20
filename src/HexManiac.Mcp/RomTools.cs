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
   [Description("Load a GBA Pokémon ROM from an absolute file path (headless mode). Must be called before headless tools.")]
   public string OpenRom(RomSession session, [Description("Absolute path to a .gba ROM file")] string path) {
      session.Load(path);
      var model = session.Require();
      return Headless(new { ok = true, path, length = model.Count, anchorCount = model.Anchors.Count });
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

   // Convert a JSON scalar argument to the CLR value the engine expects.
   private static object? JsonToValue(JsonElement v) => v.ValueKind switch {
      JsonValueKind.String => v.GetString(),
      JsonValueKind.Number => v.TryGetInt32(out var i) ? (object?)i : v.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      _ => v.ToString(),
   };

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
   [Description("Write the (possibly edited) ROM to disk. Live: saves the GUI tab to its file. Headless: defaults to the loaded path; pass outPath for a copy.")]
   public string SaveRom(
      RomSession session,
      [Description("Optional output path; defaults to the loaded ROM path (headless only)")] string? outPath = null,
      [Description("Target GUI tab by index (default: active tab)")] int? tab = null,
      [Description("Target GUI tab by filename substring")] string? tabFile = null) {
      var p = new Dictionary<string, object?>();
      if (!string.IsNullOrEmpty(outPath)) p["outPath"] = outPath;
      return Dispatch("save_rom", p, tab, tabFile, () => SaveRomHeadless(session, outPath));
   }

   private static object SaveRomHeadless(RomSession session, string? outPath) {
      var model = session.Require();
      var target = string.IsNullOrEmpty(outPath) ? session.RomPath : outPath;
      if (string.IsNullOrEmpty(target)) return RomAutomation.Err("No output path and no loaded path to default to.");
      session.RequireViewPort().ChangeHistory.ChangeCompleted();
      File.WriteAllBytes(target, model.RawData);
      return new { ok = true, path = target, length = model.RawData.Length };
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
