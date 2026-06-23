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
      sheet.clear();   // wipe values AND old formatting so each push restyles cleanly
      var values = req.values || [];
      if (values.length > 0) {
        var cols = values[0].length;
        for (var i = 0; i < values.length; i++) {
          while (values[i].length < cols) values[i].push('');   // setValues needs equal-length rows
        }
        sheet.getRange(1, 1, values.length, cols).setValues(values);
        if (req.style) applyStyle(sheet, values);
      }
      return json({ ok: true, rows: Math.max(0, values.length - 1) });
    }

    return json({ error: 'unknown action: ' + req.action });
  } catch (err) {
    return json({ error: String(err) });
  }
}

// Canonical Pokemon type colors (keyed by HMA's type names + common full-name aliases; Fairy is here for
// romhacks even though Gen 3 lacks it). Unknown types fall back to TYPE_FALLBACK.
var TYPE_COLORS = {
  'NORMAL': '#A8A878', 'FIGHT': '#C03028', 'FIGHTING': '#C03028', 'FLYING': '#A890F0',
  'POISON': '#A040A0', 'GROUND': '#E0C068', 'ROCK': '#B8A038', 'BUG': '#A8B820',
  'GHOST': '#705898', 'STEEL': '#B8B8D0', '???': '#68A090', 'CURSE': '#68A090',
  'FIRE': '#F08030', 'WATER': '#6890F0', 'GRASS': '#78C850',
  'ELECTR': '#F8D030', 'ELECTRIC': '#F8D030', 'PSYCHC': '#F85888', 'PSYCHIC': '#F85888',
  'ICE': '#98D8D8', 'DRAGON': '#7038F8', 'DARK': '#705848', 'FAIRY': '#EE99AC'
};
var TYPE_FALLBACK = '#BFBFBF';

function typeColor(v) {
  if (v === '' || v == null) return null;
  return TYPE_COLORS[String(v).toUpperCase().trim()] || TYPE_FALLBACK;
}
function contrast(hex) {   // black or white text for a given background hex
  var r = parseInt(hex.substr(1, 2), 16), g = parseInt(hex.substr(3, 2), 16), b = parseInt(hex.substr(5, 2), 16);
  return (0.299 * r + 0.587 * g + 0.114 * b) > 150 ? '#000000' : '#ffffff';
}

// Make a pushed sheet read like docs: bold + frozen header, content vertically centered, ✓/✗ cells green/
// red, a 'type' column colored by Pokemon type, number/✓/✗/type columns centered, columns auto-sized (with
// a little padding so nothing clips, capped so long text like descriptions doesn't sprawl).
function applyStyle(sheet, values) {
  var nRows = values.length, nCols = values[0].length, headers = values[0];
  var typeCol = -1;
  for (var c = 0; c < nCols; c++) if (String(headers[c]).toLowerCase() === 'type') typeCol = c;

  sheet.getRange(1, 1, 1, nCols).setFontWeight('bold').setBackground('#efefef');
  sheet.setFrozenRows(1);
  sheet.getRange(1, 1, nRows, nCols).setVerticalAlignment('middle');

  if (nRows > 1) {
    var bg = [], fc = [];
    for (var r = 1; r < nRows; r++) {
      var b = [], f = [];
      for (var c = 0; c < nCols; c++) {
        var v = values[r][c];
        if (c === typeCol) { var tc = typeColor(v); if (tc) { b.push(tc); f.push(contrast(tc)); } else { b.push('#ffffff'); f.push('#000000'); } }
        else if (v === '✓') { b.push('#d9ead3'); f.push('#38761d'); }   // green
        else if (v === '✗') { b.push('#f4cccc'); f.push('#cc0000'); }   // red
        else                { b.push('#ffffff'); f.push('#000000'); }
      }
      bg.push(b); fc.push(f);
    }
    var data = sheet.getRange(2, 1, nRows - 1, nCols);
    data.setBackgrounds(bg);
    data.setFontColors(fc);

    // center the type column and any column that's all number / ✓ / ✗ / empty (ids, stats, flag columns)
    for (var c = 0; c < nCols; c++) {
      var center = (c === typeCol);
      if (!center) {
        center = true;
        for (var r = 1; r < nRows; r++) {
          var v = values[r][c];
          if (v === '' || v === '✓' || v === '✗' || typeof v === 'number') continue;
          center = false; break;
        }
      }
      if (center) sheet.getRange(2, c + 1, nRows - 1, 1).setHorizontalAlignment('center');
    }
  }

  for (var c = 1; c <= nCols; c++) {
    sheet.autoResizeColumn(c);
    sheet.setColumnWidth(c, Math.min(sheet.getColumnWidth(c) + 20, 420));   // +20 so the last char never clips
  }
}

function doGet() {
  return json({ ok: true, note: 'HexManiac MCP sheet bridge is deployed. POST {action,token,tab,values}.' });
}

function json(obj) {
  return ContentService.createTextOutput(JSON.stringify(obj)).setMimeType(ContentService.MimeType.JSON);
}
