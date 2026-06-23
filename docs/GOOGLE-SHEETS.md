# Google Sheets integration (`sheet_push` / `sheet_pull`)

Push a ROM table to a Google Sheet, edit it in the browser, and pull it back into the ROM — fully over
the internet, no manual CSV. The integration uses **per-user OAuth**: each person authenticates their own
Google account once, and sheets are addressed by id, so it's portable to arbitrary users and their own
sheets. **You bring your own OAuth client** (a free Desktop-app client in your own Google Cloud project),
so nothing secret is shipped and there's no "unverified app" gate or shared quota.

## One-time setup (~2 minutes)

1. **Google Cloud project** — open <https://console.cloud.google.com>, create a project (or reuse one).
2. **Enable the Sheets API** — APIs & Services → Library → "Google Sheets API" → **Enable**.
3. **OAuth consent screen** — APIs & Services → OAuth consent screen → **External** → fill the app name +
   your email. Under **Test users**, add the Google account(s) you'll use. (Staying in "Testing" is fine —
   no Google verification needed for your own use; that's the point of bring-your-own-client.)
4. **Create the client** — APIs & Services → Credentials → Create credentials → **OAuth client ID** →
   Application type **Desktop app** → Create → **Download JSON**.
5. **Drop it where the MCP looks:**
   - default: `%AppData%\HexManiacMcp\google\client_secret.json` (Windows) /
     `~/.config` equivalent via `Environment.SpecialFolder.ApplicationData`, or
   - set `HEXMANIAC_GOOGLE_CLIENT_SECRET` to the file's full path, or
   - set `HEXMANIAC_GOOGLE_DIR` to choose the whole config folder.

## Use it

```json
{"name": "sheet_authenticate", "arguments": {}}
```
Opens your browser → consent once → the refresh token is cached under `<config>/tokens` (only ever sent to
Google). Future calls won't prompt until it's revoked/expired.

```json
{"name": "open_rom",   "arguments": {"path": "C:/roms/firered.gba"}}
{"name": "sheet_push", "arguments": {"table": "data.pokemon.stats", "spreadsheetId": "1AbC…", "tab": "stats"}}
```
`spreadsheetId` is the `.../d/<ID>/edit` part of the sheet URL. `sheet_push` writes a header row of field
names then one row per record (clearing the tab first).

Edit in Google Sheets, then:
```json
{"name": "sheet_pull", "arguments": {"table": "data.pokemon.stats", "spreadsheetId": "1AbC…", "tab": "stats"}}
```
`sheet_pull` matches rows by the **`index`** column (so keep it) and writes every other cell back to the ROM
as **one undo step**. The `slug` column is treated as a read-only key. Values use the same display form as
`read_table`/`write_value` (enum names like `FIRE`, numbers, text), so you can edit them in the sheet
directly. Then `save_rom` when you're happy.

## Notes & portability

- **Per user:** each user repeats the setup with *their own* client + Google account. No credentials are
  baked into the build, so there's nothing to verify with Google and no shared rate limit.
- **Headless:** these tools run against the headless session (`open_rom` first). They don't target a live
  GUI tab.
- **An embedded default client** could be added later for one-click convenience (it would then need Google
  verification to avoid the "unverified app" screen for >100 users); the bring-your-own path above always
  works regardless.
