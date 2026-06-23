using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

// Google Sheets push/pull WITHOUT Google Cloud / OAuth. Instead of the MCP authenticating to Google's
// Sheets API, the user deploys a tiny bound Apps Script web app in their sheet (docs/google-sheets-webapp.gs:
// Extensions -> Apps Script -> Deploy -> Web app), and the MCP just POSTs JSON to that URL. The script
// runs AS the sheet owner, so it edits their private sheet; the MCP only needs the secret URL + a shared
// token. Portable: each user deploys their own script -> their own URL, no Cloud project, no verification.
//
// Config: HEXMANIAC_SHEETS_URL + HEXMANIAC_SHEETS_TOKEN, or <AppData>/HexManiacMcp/google/sheets.json
// ({"url": "...", "token": "..."}); the url can also be passed per call. The ROM side reuses RomAutomation
// so the row<->field shape matches read_table/export_table/write_value.
public static class GoogleSheetsService {
   private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

   private static string ConfigDir {
      get {
         var baseDir = Environment.GetEnvironmentVariable("HEXMANIAC_GOOGLE_DIR");
         if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HexManiacMcp", "google");
         Directory.CreateDirectory(baseDir);
         return baseDir;
      }
   }

   private static (string url, string token) Config(string urlOverride) {
      string url = string.IsNullOrWhiteSpace(urlOverride) ? Environment.GetEnvironmentVariable("HEXMANIAC_SHEETS_URL") : urlOverride;
      string token = Environment.GetEnvironmentVariable("HEXMANIAC_SHEETS_TOKEN");
      var cfg = Path.Combine(ConfigDir, "sheets.json");
      if ((string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token)) && File.Exists(cfg)) {
         try {
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (string.IsNullOrEmpty(url) && doc.RootElement.TryGetProperty("url", out var u)) url = u.GetString();
            if (string.IsNullOrEmpty(token) && doc.RootElement.TryGetProperty("token", out var t)) token = t.GetString();
         } catch { }
      }
      return (url, token);
   }

   private static JsonElement Post(string url, object payload) {
      var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
      var resp = Http.PostAsync(url, content).GetAwaiter().GetResult();            // HttpClient follows Apps Script's 302 to the result
      var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
      try { return JsonDocument.Parse(body).RootElement.Clone(); }
      catch { throw new InvalidOperationException($"Web app didn't return JSON (HTTP {(int)resp.StatusCode}). Check the deployment is a Web app with access 'Anyone with the link'. First 200 chars: {body[..Math.Min(200, body.Length)]}"); }
   }

   public static object PushTable(IDataModel model, string table, string tab, string urlOverride) {
      var (url, token) = Config(urlOverride);
      if (string.IsNullOrEmpty(url)) return RomAutomation.Err("No web-app URL. Set HEXMANIAC_SHEETS_URL (or <config>/sheets.json), or pass url. See docs/GOOGLE-SHEETS.md.");
      if (RomAutomation.ReadTable(model, table, 0, int.MaxValue) is not IDictionary<string, object?> read) return RomAutomation.Err("read failed");
      if (read.ContainsKey("error")) return read;
      var fields = ((System.Collections.IEnumerable)read["fields"]!).Cast<object>().Select(f => f?.ToString() ?? "").ToList();
      // 'index' (the ROM row id) isn't one of the table's fields - it's a separate key on each row - but it
      // MUST be the first column so sheet_pull can match rows back. Prepend it; 'slug' is already in fields.
      var columns = new List<string> { "index" };
      columns.AddRange(fields);
      var values = new List<List<object?>> { columns.Cast<object?>().ToList() };
      foreach (var rObj in (System.Collections.IEnumerable)read["rows"]!) {
         var row = (IDictionary<string, object?>)rObj;
         values.Add(columns.Select(f => row.TryGetValue(f, out var v) ? Cell(v) : "").ToList());
      }
      var res = Post(url, new { action = "push", token, tab, values });
      if (res.TryGetProperty("error", out var e)) return RomAutomation.Err("web app: " + e.GetString());
      return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["tab"] = tab, ["rowsWritten"] = values.Count - 1, ["columns"] = columns.Count };
   }

   public static object PullTable(IDataModel model, Func<ModelDelta> changeToken, string table, string tab, string urlOverride) {
      var (url, token) = Config(urlOverride);
      if (string.IsNullOrEmpty(url)) return RomAutomation.Err("No web-app URL. Set HEXMANIAC_SHEETS_URL (or <config>/sheets.json), or pass url. See docs/GOOGLE-SHEETS.md.");
      var res = Post(url, new { action = "pull", token, tab });
      if (res.TryGetProperty("error", out var e)) return RomAutomation.Err("web app: " + e.GetString());
      if (!res.TryGetProperty("values", out var grid) || grid.ValueKind != JsonValueKind.Array || grid.GetArrayLength() < 2)
         return RomAutomation.Err("Sheet has no data rows (need a header row plus at least one record). Push first to lay it out.");

      var data = grid.EnumerateArray().Select(r => r.EnumerateArray().Select(CellStr).ToList()).ToList();
      var headers = data[0];
      int indexCol = headers.FindIndex(h => h.Equals("index", StringComparison.OrdinalIgnoreCase));
      if (indexCol < 0) return RomAutomation.Err("The sheet needs an 'index' column (the ROM row id) to match rows back. Push first to get the layout.");
      var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index", "slug" };  // key + read-only derived

      // current ROM values (same display form sheet_push wrote), so we only write cells that ACTUALLY
      // changed. Writing every cell back (10k+ for big tables) is needless and can exhaust the host;
      // a normal edit touches a handful.
      var current = new Dictionary<int, Dictionary<string, string>>();
      if (RomAutomation.ReadTable(model, table, 0, int.MaxValue) is IDictionary<string, object?> cur && !cur.ContainsKey("error")) {
         foreach (var rObj in (System.Collections.IEnumerable)cur["rows"]!) {
            var rd = (IDictionary<string, object?>)rObj;
            if (rd.TryGetValue("index", out var iv) && iv is int ci) {
               var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
               foreach (var kv in rd) m[kv.Key] = kv.Value?.ToString() ?? "";
               current[ci] = m;
            }
         }
      }

      int writes = 0, rowsTouched = 0, skipped = 0; var errors = new List<string>();
      for (int r = 1; r < data.Count; r++) {
         var row = data[r];
         if (row.Count <= indexCol || !int.TryParse(row[indexCol], out var index)) continue;
         current.TryGetValue(index, out var cur1);
         bool touched = false;
         for (int c = 0; c < headers.Count && c < row.Count; c++) {
            var field = headers[c];
            if (string.IsNullOrEmpty(field) || skip.Contains(field)) continue;
            if (cur1 != null && cur1.TryGetValue(field, out var old) && old == row[c]) { skipped++; continue; }  // unchanged
            var res2 = RomAutomation.WriteValue(model, changeToken, table, index, field, row[c]) as IDictionary<string, object?>;
            if (res2 != null && res2.ContainsKey("error")) { if (errors.Count < 10) errors.Add($"[{index}].{field}: {res2["error"]}"); }
            else { writes++; touched = true; }
         }
         if (touched) rowsTouched++;
      }
      return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["tab"] = tab, ["rowsUpdated"] = rowsTouched, ["cellsWritten"] = writes, ["cellsUnchanged"] = skipped, ["errors"] = errors };
   }

   // Push records from a JSON file (an export_* output) to a tab as a flattened READ-ONLY reference view:
   // lists are joined with ", ", nested objects become compact JSON. Not pulled back (columns are derived,
   // not raw table fields) - use sheet_push/sheet_pull for editable round-trips.
   public static object PushJson(string path, string key, string tab, string urlOverride, string explode, bool style) {
      var (url, token) = Config(urlOverride);
      if (string.IsNullOrEmpty(url)) return RomAutomation.Err("No web-app URL. Set HEXMANIAC_SHEETS_URL (or <config>/sheets.json), or pass url. See docs/GOOGLE-SHEETS.md.");
      if (!File.Exists(path)) return RomAutomation.Err($"File not found: {path}");
      using var doc = JsonDocument.Parse(File.ReadAllText(path));
      var root = doc.RootElement;
      JsonElement arr;
      if (string.IsNullOrWhiteSpace(key)) { if (root.ValueKind != JsonValueKind.Array) return RomAutomation.Err("Root isn't an array - pass key (the array property to push, e.g. 'moves')."); arr = root; }
      else if (!root.TryGetProperty(key, out arr) || arr.ValueKind != JsonValueKind.Array) return RomAutomation.Err($"'{key}' isn't an array in {Path.GetFileName(path)}.");

      var baseCols = new List<string>(); var seen = new HashSet<string>();
      foreach (var rec in arr.EnumerateArray())
         if (rec.ValueKind == JsonValueKind.Object)
            foreach (var p in rec.EnumerateObject()) if (seen.Add(p.Name)) baseCols.Add(p.Name);
      if (baseCols.Count == 0) return RomAutomation.Err("No object records to push.");

      // explode: turn a list-valued column (e.g. "flags") into one ✓/✗ column per distinct value.
      var explodeVals = new List<string>(); var evSeen = new HashSet<string>();
      bool doExplode = !string.IsNullOrWhiteSpace(explode) && baseCols.Contains(explode);
      if (doExplode) {
         foreach (var rec in arr.EnumerateArray())
            if (rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty(explode, out var lv) && lv.ValueKind == JsonValueKind.Array)
               foreach (var el in lv.EnumerateArray()) { var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText(); if (!string.IsNullOrEmpty(s) && evSeen.Add(s)) explodeVals.Add(s); }
         doExplode = explodeVals.Count > 0;
      }
      var explodeSet = new HashSet<string>(explodeVals);
      var columns = new List<string>();
      foreach (var col in baseCols) { if (doExplode && col == explode) columns.AddRange(explodeVals); else columns.Add(col); }

      var values = new List<List<object?>> { columns.Cast<object?>().ToList() };
      foreach (var rec in arr.EnumerateArray()) {
         var present = new HashSet<string>();
         if (doExplode && rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty(explode, out var lv2) && lv2.ValueKind == JsonValueKind.Array)
            foreach (var el in lv2.EnumerateArray()) present.Add(el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText());
         var row = new List<object?>();
         foreach (var oc in columns) {
            if (explodeSet.Contains(oc)) row.Add(present.Contains(oc) ? "✓" : "✗");
            else row.Add(rec.ValueKind == JsonValueKind.Object && rec.TryGetProperty(oc, out var v) ? Flatten(v) : "");
         }
         values.Add(row);
      }
      var res = Post(url, new { action = "push", token, tab, values, style });
      if (res.TryGetProperty("error", out var e)) return RomAutomation.Err("web app: " + e.GetString());
      return new Dictionary<string, object?> { ["ok"] = true, ["source"] = Path.GetFileName(path), ["key"] = key, ["tab"] = tab, ["rowsWritten"] = values.Count - 1, ["columns"] = columns.Count, ["explodedInto"] = doExplode ? explodeVals.Count : 0, ["styled"] = style, ["note"] = "Read-only reference view; not pulled back." };
   }

   private static object? Flatten(JsonElement v) => v.ValueKind switch {
      JsonValueKind.String => v.GetString(),
      JsonValueKind.Number => v.TryGetInt64(out var l) ? l : v.GetDouble(),
      JsonValueKind.True => true,
      JsonValueKind.False => false,
      JsonValueKind.Null => "",
      JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())),
      JsonValueKind.Object => v.GetRawText(),
      _ => v.GetRawText(),
   };

   private static string CellStr(JsonElement c) => c.ValueKind switch {
      JsonValueKind.String => c.GetString() ?? "",
      JsonValueKind.Null => "",
      JsonValueKind.True => "true",
      JsonValueKind.False => "false",
      _ => c.GetRawText(),
   };
   private static object? Cell(object? v) => v switch {
      null => "",
      string or bool or int or long or double or float => v,
      _ => v.ToString(),
   };
}
