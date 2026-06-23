/**
 * HexManiacAdvance MCP <-> Google Sheets bridge (no Google Cloud / OAuth needed).
 *
 * Setup (in the sheet you want to sync):
 *   1. Extensions -> Apps Script. Delete the stub, paste this whole file.
 *   2. Change SECRET below to a long random string of your own.
 *   3. Limit the script to THIS sheet only (not all your spreadsheets): Project Settings (gear) ->
 *      check "Show appsscript.json manifest file in editor", open appsscript.json, and add:
 *        "oauthScopes": ["https://www.googleapis.com/auth/spreadsheets.currentonly"]
 *      (per-file scope - the consent then says "only the specific spreadsheet you use this app with").
 *      This works because the script only touches getActiveSpreadsheet(); never use openById/openByUrl,
 *      which would force the all-spreadsheets scope.
 *   4. Deploy -> New deployment -> type "Web app":
 *        Execute as: Me        Who has access: Anyone with the link
 *      Authorize when prompted (this is the Apps Script editor's own consent - NOT Google Cloud).
 *   6. Copy the Web app URL.
 *   7. Tell the MCP: set HEXMANIAC_SHEETS_URL = that URL and HEXMANIAC_SHEETS_TOKEN = your SECRET,
 *      or write <AppData>/HexManiacMcp/google/sheets.json = {"url":"...","token":"..."}.
 *
 * The MCP POSTs {action, token, tab, values}. push writes the grid; pull returns it. The script runs
 * as you, so it edits your private sheet; the caller only needs the secret URL + token.
 */
var SECRET = 'change-me-to-a-long-random-string';

function doPost(e) {
  try {
    var req = JSON.parse(e.postData.contents);
    if (req.token !== SECRET) return json({ error: 'bad token' });

    var ss = SpreadsheetApp.getActiveSpreadsheet();
    var sheet = req.tab ? ss.getSheetByName(req.tab) : ss.getSheets()[0];

    if (req.action === 'pull') {
      if (!sheet) return json({ error: 'no such tab: ' + req.tab });
      return json({ ok: true, values: sheet.getDataRange().getValues() });
    }

    if (req.action === 'push') {
      if (!sheet) sheet = req.tab ? ss.insertSheet(req.tab) : ss.getSheets()[0];
      sheet.clearContents();
      var values = req.values || [];
      if (values.length > 0) {
        var cols = values[0].length;
        // normalize ragged rows to a rectangle (setValues requires equal-length rows)
        for (var i = 0; i < values.length; i++) {
          while (values[i].length < cols) values[i].push('');
        }
        sheet.getRange(1, 1, values.length, cols).setValues(values);
      }
      return json({ ok: true, rows: Math.max(0, values.length - 1) });
    }

    return json({ error: 'unknown action: ' + req.action });
  } catch (err) {
    return json({ error: String(err) });
  }
}

function doGet() {
  return json({ ok: true, note: 'HexManiac MCP sheet bridge is deployed. POST {action,token,tab,values}.' });
}

function json(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}
