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
      var values = new List<List<object?>> { fields.Cast<object?>().ToList() };
      foreach (var rObj in (System.Collections.IEnumerable)read["rows"]!) {
         var row = (IDictionary<string, object?>)rObj;
         values.Add(fields.Select(f => row.TryGetValue(f, out var v) ? Cell(v) : "").ToList());
      }
      var res = Post(url, new { action = "push", token, tab, values });
      if (res.TryGetProperty("error", out var e)) return RomAutomation.Err("web app: " + e.GetString());
      return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["tab"] = tab, ["rowsWritten"] = values.Count - 1, ["columns"] = fields.Count };
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
      int writes = 0, rowsTouched = 0; var errors = new List<string>();
      for (int r = 1; r < data.Count; r++) {
         var row = data[r];
         if (row.Count <= indexCol || !int.TryParse(row[indexCol], out var index)) continue;
         bool touched = false;
         for (int c = 0; c < headers.Count && c < row.Count; c++) {
            var field = headers[c];
            if (string.IsNullOrEmpty(field) || skip.Contains(field)) continue;
            var res2 = RomAutomation.WriteValue(model, changeToken, table, index, field, row[c]) as IDictionary<string, object?>;
            if (res2 != null && res2.ContainsKey("error")) { if (errors.Count < 10) errors.Add($"[{index}].{field}: {res2["error"]}"); }
            else { writes++; touched = true; }
         }
         if (touched) rowsTouched++;
      }
      return new Dictionary<string, object?> { ["ok"] = true, ["table"] = table, ["tab"] = tab, ["rowsUpdated"] = rowsTouched, ["cellsWritten"] = writes, ["errors"] = errors };
   }

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
