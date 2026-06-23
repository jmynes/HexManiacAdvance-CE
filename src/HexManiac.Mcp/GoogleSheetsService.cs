using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;
using Google.Apis.Util.Store;
using HavenSoft.HexManiac.Core.Models;

namespace HavenSoft.HexManiac.Mcp;

// Google Sheets push/pull for the MCP (Model-1 OAuth: the integration lives in the server, each USER
// authenticates their OWN Google account once via the desktop loopback flow, and sheets are addressed
// by spreadsheet id). Portable across arbitrary users/sheets by design:
//   * the OAuth *client* is brought by the user (a Desktop-app client they create in their own Google
//     Cloud project) - dropped at <config>/client_secret.json, or pointed to by HEXMANIAC_GOOGLE_CLIENT_SECRET.
//     No secret is shipped, so there's no "unverified app" gate and no shared quota. (An embedded default
//     client could be added later for convenience without changing this code path.)
//   * each user's refresh token is cached locally under <config>/tokens, never sent anywhere but Google.
// Spreadsheet ids come from the sheet URL (.../d/<ID>/edit). The ROM side reuses RomAutomation, so the
// row<->field shape matches read_table/export_table/write_value.
public static class GoogleSheetsService {
   private static readonly string[] Scopes = { SheetsService.Scope.Spreadsheets };
   private const string AppName = "HexManiacAdvance MCP";

   private static string ConfigDir {
      get {
         var baseDir = Environment.GetEnvironmentVariable("HEXMANIAC_GOOGLE_DIR");
         if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HexManiacMcp", "google");
         Directory.CreateDirectory(baseDir);
         return baseDir;
      }
   }

   private static string ClientSecretPath() {
      var env = Environment.GetEnvironmentVariable("HEXMANIAC_GOOGLE_CLIENT_SECRET");
      if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;
      var p = Path.Combine(ConfigDir, "client_secret.json");
      return File.Exists(p) ? p : null;
   }

   // Run/refresh the desktop OAuth flow and cache the user's token. First call opens the browser.
   private static UserCredential Authorize() {
      var secretPath = ClientSecretPath();
      if (secretPath == null)
         throw new InvalidOperationException(
            $"No Google OAuth client found. Create a Desktop-app OAuth client in your Google Cloud project (enable the Sheets API), download it, and save it as '{Path.Combine(ConfigDir, "client_secret.json")}' (or set HEXMANIAC_GOOGLE_CLIENT_SECRET). See docs/GOOGLE-SHEETS.md.");
      using var stream = new FileStream(secretPath, FileMode.Open, FileAccess.Read);
      var secrets = GoogleClientSecrets.FromStream(stream).Secrets;
      var tokenStore = new FileDataStore(Path.Combine(ConfigDir, "tokens"), true);
      return GoogleWebAuthorizationBroker.AuthorizeAsync(secrets, Scopes, "user", CancellationToken.None, tokenStore)
         .GetAwaiter().GetResult();
   }

   private static SheetsService Service() =>
      new(new BaseClientService.Initializer { HttpClientInitializer = Authorize(), ApplicationName = AppName });

   // Trigger (or refresh) authentication; returns the signed-in account if the API exposes it.
   public static object Authenticate() {
      var service = Service();
      return new Dictionary<string, object?> {
         ["ok"] = true,
         ["configDir"] = ConfigDir,
         ["clientSecret"] = ClientSecretPath(),
         ["note"] = "Authenticated. Token cached locally; future calls won't prompt until it's revoked/expired.",
      };
   }

   private static string A1(string tab) => string.IsNullOrWhiteSpace(tab) ? null : $"'{tab.Replace("'", "''")}'";
   private static string Range(string tab) => A1(tab) is string t ? $"{t}!A1:ZZ" : "A1:ZZ";

   // ROM table -> sheet tab: header row of field names, then one row per record. Clears the tab first.
   public static object PushTable(IDataModel model, string table, string spreadsheetId, string tab) {
      if (string.IsNullOrWhiteSpace(spreadsheetId)) return RomAutomation.Err("spreadsheetId is required (the .../d/<ID>/edit part of the sheet URL).");
      if (RomAutomation.ReadTable(model, table, 0, int.MaxValue) is not IDictionary<string, object?> read) return RomAutomation.Err("read failed");
      if (read.ContainsKey("error")) return read;
      var fields = ((System.Collections.IEnumerable)read["fields"]!).Cast<object>().Select(f => f?.ToString() ?? "").ToList();
      var values = new List<IList<object>> { fields.Cast<object>().ToList() };
      foreach (var rObj in (System.Collections.IEnumerable)read["rows"]!) {
         var row = (IDictionary<string, object?>)rObj;
         values.Add(fields.Select(f => row.TryGetValue(f, out var v) ? Cell(v) : "").ToList());
      }
      var service = Service();
      try { service.Spreadsheets.Values.Clear(new ClearValuesRequest(), spreadsheetId, Range(tab)).Execute(); } catch { }
      var body = new ValueRange { Values = values };
      var update = service.Spreadsheets.Values.Update(body, spreadsheetId, A1(tab) is string t ? $"{t}!A1" : "A1");
      update.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.RAW;
      var resp = update.Execute();
      return new Dictionary<string, object?> {
         ["ok"] = true, ["table"] = table, ["spreadsheetId"] = spreadsheetId, ["tab"] = tab,
         ["rowsWritten"] = values.Count - 1, ["columns"] = fields.Count, ["updatedCells"] = resp.UpdatedCells,
      };
   }

   // Sheet tab -> ROM table: header row maps columns to fields; the 'index' column locates each ROM row.
   // Writes every non-key, non-derived cell back as ONE undo step (via the supplied change token).
   public static object PullTable(IDataModel model, Func<HavenSoft.HexManiac.Core.Models.ModelDelta> token, string table, string spreadsheetId, string tab) {
      if (string.IsNullOrWhiteSpace(spreadsheetId)) return RomAutomation.Err("spreadsheetId is required.");
      var service = Service();
      var data = service.Spreadsheets.Values.Get(spreadsheetId, Range(tab)).Execute().Values;
      if (data == null || data.Count < 2) return RomAutomation.Err("Sheet has no data rows (need a header row plus at least one record).");
      var headers = data[0].Select(h => h?.ToString() ?? "").ToList();
      int indexCol = headers.FindIndex(h => h.Equals("index", StringComparison.OrdinalIgnoreCase));
      if (indexCol < 0) return RomAutomation.Err("The sheet needs an 'index' column (the ROM row id) so rows can be matched back. Push first to get the right layout.");
      var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index", "slug" };  // key + read-only derived
      int writes = 0, rowsTouched = 0; var errors = new List<string>();
      for (int r = 1; r < data.Count; r++) {
         var row = data[r];
         if (row.Count <= indexCol || !int.TryParse(row[indexCol]?.ToString(), out var index)) continue;
         bool touched = false;
         for (int c = 0; c < headers.Count && c < row.Count; c++) {
            var field = headers[c];
            if (string.IsNullOrEmpty(field) || skip.Contains(field)) continue;
            var cell = row[c]?.ToString();
            if (cell == null) continue;
            var res = RomAutomation.WriteValue(model, token, table, index, field, cell) as IDictionary<string, object?>;
            if (res != null && res.ContainsKey("error")) { if (errors.Count < 10) errors.Add($"[{index}].{field}: {res["error"]}"); }
            else { writes++; touched = true; }
         }
         if (touched) rowsTouched++;
      }
      return new Dictionary<string, object?> {
         ["ok"] = true, ["table"] = table, ["spreadsheetId"] = spreadsheetId, ["tab"] = tab,
         ["rowsUpdated"] = rowsTouched, ["cellsWritten"] = writes, ["errors"] = errors,
      };
   }

   // sheets take primitives; everything else (the few list/object fields) becomes its string form.
   private static object Cell(object v) => v switch {
      null => "",
      string or bool or int or long or double or float => v,
      _ => v.ToString(),
   };
}
