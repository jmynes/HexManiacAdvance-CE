#!/usr/bin/env bash
# Deterministic smoke test for the HexManiac MCP server.
# Builds the server, drives it over stdio (newline-delimited JSON-RPC), and
# asserts each capability in the definition-of-done. Exit 0 only if ALL pass.
#
# This is the promise gate for the Ralph loop. Requirements: dotnet (SDK 8),
# jq, and a clean FireRed at test/roms/firered.gba.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
export PATH="/c/Program Files/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

ROM="$(pwd)/test/roms/Pokemon - FireRed Version (USA).gba"
TMP="$(pwd)/test/.tmp"
OUT="$TMP/out.jsonl"
ERR="$TMP/err.txt"
EXE="artifacts/HexManiac.Mcp/bin/Release/net8.0/HexManiac.Mcp.exe"
mkdir -p "$TMP"
# Kill any stray server from a previous/interrupted run so it can't lock the exe.
taskkill //F //IM HexManiac.Mcp.exe >/dev/null 2>&1 || true
PASS=0; FAIL=0
ok()   { echo "  PASS: $1"; PASS=$((PASS+1)); }
bad()  { echo "  FAIL: $1"; FAIL=$((FAIL+1)); }

echo "== 1. build =="
# Build from the MCP project dir so its project-local global.json (SDK 8) applies.
if ( cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release -v quiet ) >/dev/null 2>&1; then ok "build"; else bad "build (cd src/HexManiac.Mcp && dotnet build to see errors)"; echo "BUILD FAILED — stopping"; exit 1; fi
[ -f "$ROM" ] || { echo "missing test ROM at $ROM"; exit 2; }

# --- helper: pull the inner tool-result JSON for a given response id ---
result_text() { jq -rs --argjson id "$1" 'map(select(.id==$id))[0].result.content[0].text // empty' "$OUT"; }
is_error()    { jq -rs --argjson id "$1" 'map(select(.id==$id))[0].result.isError // false' "$OUT"; }

OUTROM="$TMP/firered.edited.gba"
ENC="$TMP/encounters.json"; TRN="$TMP/trainers.json"; DEX="$TMP/dex.json"
# Convert MSYS paths (/q/Users/...) to Windows form (Q:/Users/...) that .NET
# can open. Forward slashes are valid on Windows and dodge JSON-escape issues.
R="$(cygpath -m "$ROM")"; OR="$(cygpath -m "$OUTROM")"
ENCW="$(cygpath -m "$ENC")"; TRNW="$(cygpath -m "$TRN")"; DEXW="$(cygpath -m "$DEX")"
FAKE="$TMP/fakerom.gba"; cp "$ROM" "$FAKE"; printf 'ZZZZ' | dd of="$FAKE" bs=1 seek=172 count=4 conv=notrunc 2>/dev/null; rm -f "$TMP/fakerom.toml"
FAKEW="$(cygpath -m "$FAKE")"

echo "== 2-6. drive server =="
{
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":99}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.moves.names","index":19,"field":"name","value":"SMOKETEST"}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.moves.names","start":19,"count":1}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"type1","value":"FLYING"}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"type1","value":"NOTATYPE"}}}'; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"save_rom\",\"arguments\":{\"outPath\":\"$OR\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 2
  # re-open original so exports reflect the clean ROM
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\"}}}"; sleep 15
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"export_table\",\"arguments\":{\"name\":\"data.pokemon.wild\",\"outPath\":\"$ENCW\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"export_table\",\"arguments\":{\"name\":\"data.trainers.stats\",\"outPath\":\"$TRNW\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"export_table\",\"arguments\":{\"name\":\"data.pokedex.stats\",\"outPath\":\"$DEXW\"}}}"; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"run_script","arguments":{"script":"","path":"resources/Scripts/Add Mechanics From Later Generations/AnyGame_PixilateStyleAbilities.hma"}}}'; sleep 8
  printf '%s\n' '{"jsonrpc":"2.0","id":24,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":123}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":25,"method":"tools/call","params":{"name":"undo","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":26,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":27,"method":"tools/call","params":{"name":"copy_rows","arguments":{"table":"data.pokemon.stats","index":1,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":28,"method":"tools/call","params":{"name":"paste_rows","arguments":{"table":"data.pokemon.stats","index":4}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":29,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":4,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"select","arguments":{"table":"data.pokemon.stats","index":1,"count":2}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":32,"method":"tools/call","params":{"name":"clipboard_copy","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"goto","arguments":{"target":"Pokemon"}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":33,"method":"tools/call","params":{"name":"close_tab","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":34,"method":"tools/call","params":{"name":"close_rom","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":35,"method":"tools/call","params":{"name":"duplicate_tab","arguments":{}}}'; sleep 1
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":36,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\",\"metadata\":\"guess_offsets\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":37,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$FAKEW\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":38,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$FAKEW\",\"metadata\":\"guess\"}}}"; sleep 8
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":41,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":39,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.moves.stats.battle","index":1,"field":"info","value":false,"flag":"Makes Contact"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":40,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.moves.stats.battle","index":1,"field":"target","value":true,"flag":"Both"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":42,"method":"tools/call","params":{"name":"save_rom","arguments":{}}}'; sleep 1
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":44,"method":"tools/call","params":{"name":"save_rom","arguments":{"overwrite":true}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":45,"method":"tools/call","params":{"name":"help","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":46,"method":"tools/call","params":{"name":"help","arguments":{"topic":"write_value"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":47,"method":"tools/call","params":{"name":"help","arguments":{"topic":"nonsense_zzz"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":48,"method":"resources/list","params":{}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":49,"method":"resources/read","params":{"uri":"hexmaniac://guide"}}'; sleep 1
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":50,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":51,"method":"tools/call","params":{"name":"backup_rom","arguments":{}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":52,"method":"tools/call","params":{"name":"save_rom","arguments":{"overwrite":true}}}'; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":53,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":54,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":"55"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":55,"method":"tools/call","params":{"name":"launch_rom","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":56,"method":"tools/call","params":{"name":"supported_roms","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":57,"method":"tools/call","params":{"name":"supported_roms","arguments":{"code":"BPEE0"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":58,"method":"resources/list","params":{}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":59,"method":"resources/read","params":{"uri":"hexmaniac://supported-roms"}}'; sleep 1
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":60,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":61,"method":"tools/call","params":{"name":"identify_rom","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":62,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":"61"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":63,"method":"tools/call","params":{"name":"identify_rom","arguments":{}}}'; sleep 1
} | "./$EXE" > "$OUT" 2>"$ERR"

echo "== assertions =="
# 2. protocol + tools/list
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "31" ] && ok "tools/list shows 31 tools" || bad "tools/list"
# 3. open + read known value
[ "$(is_error 3)" = "false" ] && ok "open_rom" || bad "open_rom"
[ "$(result_text 4 | jq -r '.rows[0].hp' 2>/dev/null)" = "45" ] && ok "read_table Bulbasaur hp=45" || bad "read_table known value"
# 4. write + save + reload reflects change
[ "$(is_error 5)" = "false" ] && ok "write_value" || bad "write_value (TODO)"
[ "$(is_error 6)" = "false" ] && [ -f "$OUTROM" ] && ok "save_rom wrote file" || bad "save_rom (TODO)"
[ "$(result_text 8 | jq -r '.rows[0].hp' 2>/dev/null)" = "99" ] && ok "edit persisted (hp=99 after reload)" || bad "edit did not persist (TODO)"
# typed writes (string + enum-by-name) and a bad-enum error
[ "$(result_text 20 | jq -r '.newValue' 2>/dev/null)" = "SMOKETEST" ] && ok "write_value string newValue" || bad "write_value string"
[ "$(result_text 21 | jq -r '.rows[0].name' 2>/dev/null)" = "SMOKETEST" ] && ok "string write read-back" || bad "string write read-back"
[ "$(result_text 22 | jq -r '.newValue' 2>/dev/null)" = "FLYING" ] && ok "write_value enum-by-name" || bad "write_value enum"
[ "$(result_text 23 | jq -r '.error' 2>/dev/null | grep -ci 'Options')" -ge 1 ] && ok "bad enum lists options" || bad "bad enum error"
# undo/redo
[ "$(result_text 25 | jq -r '.applied' 2>/dev/null)" -ge 1 ] && ok "undo applied >=1" || bad "undo applied"
[ "$(result_text 26 | jq -r '.rows[0].hp' 2>/dev/null)" != "123" ] && ok "undo reverted hp write" || bad "undo did not revert"
# copy_rows / paste_rows
[ -n "$(result_text 27 | jq -r '.bytes // empty' 2>/dev/null)" ] && ok "copy_rows returns bytes" || bad "copy_rows bytes"
[ "$(result_text 28 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "paste_rows ok" || bad "paste_rows"
[ "$(result_text 29 | jq -r '.rows[0].hp' 2>/dev/null)" = "$(result_text 30 | jq -r '.rows[0].hp' 2>/dev/null)" ] && ok "paste cloned row (hp matches)" || bad "paste clone"
# 5. exports
[ "$(is_error 10)" = "false" ] && [ -s "$ENC" ] && ok "export encounters" || bad "export encounters (TODO)"
[ "$(is_error 11)" = "false" ] && [ -s "$TRN" ] && ok "export trainers" || bad "export trainers (TODO)"
[ "$(is_error 12)" = "false" ] && [ -s "$DEX" ] && ok "export dex" || bad "export dex (TODO)"
# 6. script (logical failures don't set isError, so check the inner ok flag)
[ "$(result_text 13 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "run_script (no errors)" || bad "run_script (TODO)"
# 7. list_shortcuts (headless): correct mode + array shape
[ "$(result_text 14 | jq -r '.mode' 2>/dev/null)" = "headless" ] && ok "list_shortcuts mode=headless" || bad "list_shortcuts mode"
[ "$(result_text 14 | jq -r '.shortcuts|type' 2>/dev/null)" = "array" ] && ok "list_shortcuts returns array" || bad "list_shortcuts shape"
# 8. goto (headless): live-only error + correct mode
[ "$(result_text 15 | jq -r '.mode' 2>/dev/null)" = "headless" ] && ok "goto mode=headless" || bad "goto mode"
[ "$(result_text 15 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "goto headless live-only error" || bad "goto headless error"
# select (headless): live-only error
[ "$(result_text 31 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "select live-only in headless" || bad "select headless error"
# clipboard_copy (headless): live-only error
[ "$(result_text 32 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "clipboard_copy live-only in headless" || bad "clipboard headless error"
# close_tab/close_rom (headless): live-only errors
[ "$(result_text 33 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "close_tab live-only in headless" || bad "close_tab headless error"
[ "$(result_text 34 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "close_rom live-only in headless" || bad "close_rom headless error"
[ "$(result_text 35 | jq -r '.error' 2>/dev/null | grep -ci 'live GUI')" -ge 1 ] && ok "duplicate_tab live-only in headless" || bad "duplicate_tab headless error"
# open_rom metadata awareness
[ "$(result_text 3 | jq -r '.recognized' 2>/dev/null)" = "true" ] && ok "open_rom reports recognized firered" || bad "open_rom recognized"
[ -n "$(result_text 3 | jq -r '.gameCode // empty' 2>/dev/null)" ] && ok "open_rom reports gameCode" || bad "open_rom gameCode"
[ "$(result_text 36 | jq -r '.error' 2>/dev/null | grep -ci 'not yet implemented')" -ge 1 ] && ok "guess_offsets not-implemented notice" || bad "guess_offsets notice"
[ "$(result_text 37 | jq -r '.needsMetadataChoice' 2>/dev/null)" = "true" ] && ok "unrecognized auto -> needsMetadataChoice" || bad "unrecognized warning"
[ "$(result_text 38 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "guess opens unrecognized rom" || bad "guess open"
[ "$(result_text 39 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "checkbox by spaced friendly name" || bad "spaced flag name"
[ "$(result_text 39 | jq -r '.flag' 2>/dev/null)" = "Makes Contact" ] && ok "flag reported unquoted" || bad "flag unquoted report"
[ "$(result_text 40 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "single-word flag still works" || bad "single-word flag"
# save_rom overwrite guard
[ "$(result_text 42 | jq -r '.error' 2>/dev/null | grep -ci 'Refusing to overwrite')" -ge 1 ] && ok "save_rom guards in-place overwrite" || bad "save_rom guard"
[ "$(result_text 44 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "save_rom overwrite=true saves in place" || bad "save_rom overwrite"
[ "$(result_text 44 | jq -r '.overwrote' 2>/dev/null)" = "true" ] && ok "save_rom reports overwrote" || bad "save_rom overwrote flag"
# help tool
[ "$(result_text 45 2>/dev/null | grep -ci 'open_rom')" -ge 1 ] && ok "help overview mentions open_rom" || bad "help overview"
[ "$(result_text 46 2>/dev/null | grep -ci 'write_value')" -ge 1 ] && ok "help topic returns section" || bad "help topic"
[ "$(result_text 47 2>/dev/null | grep -ci 'section')" -ge 1 ] && ok "help unknown topic lists sections" || bad "help unknown topic"
# resources
[ "$(jq -rs 'map(select(.id==48))[0].result.resources | map(.uri) | join(" ")' "$OUT" 2>/dev/null | grep -ci 'hexmaniac://guide')" -ge 1 ] && ok "resources/list has guide" || bad "resources/list"
[ "$(jq -rs 'map(select(.id==49))[0].result.contents[0].text // empty' "$OUT" 2>/dev/null | grep -ci 'open_rom')" -ge 1 ] && ok "resources/read guide non-empty" || bad "resources/read"
[ "$(result_text 51 | jq -r '.files | length' 2>/dev/null)" -ge 1 ] && ok "backup_rom created files" || bad "backup_rom files"
[ "$(result_text 52 | jq -r '.backedUp | length' 2>/dev/null)" -ge 1 ] && ok "save_rom overwrite auto-backs-up" || bad "save_rom backedUp"
[ "$(result_text 55 | jq -r '.error' 2>/dev/null | grep -ci 'unsaved changes')" -ge 1 ] && ok "launch_rom refuses when unsaved" || bad "launch_rom unsaved guard"
[ "$(result_text 56 | grep -ci 'BPRE0')" -ge 1 ] && ok "supported_roms lists BPRE0" || bad "supported_roms all"
[ "$(result_text 57 | grep -ci 'Emerald')" -ge 1 ] && ok "supported_roms code filter" || bad "supported_roms code"
[ "$(jq -rs 'map(select(.id==58))[0].result.resources|map(.uri)|join(" ")' "$OUT" 2>/dev/null | grep -ci 'hexmaniac://supported-roms')" -ge 1 ] && ok "resources/list has supported-roms" || bad "resources/list roms"
[ "$(jq -rs 'map(select(.id==59))[0].result.contents[0].text // empty' "$OUT" 2>/dev/null | grep -ci 'BPRE0')" -ge 1 ] && ok "resources/read supported-roms" || bad "resources/read roms"
[ "$(result_text 61 | jq -r '.isCleanDump' 2>/dev/null)" = "true" ] && ok "identify_rom clean FireRed" || bad "identify clean"
[ "$(result_text 61 | jq -r '.baseGame' 2>/dev/null | grep -ci 'FireRed')" -ge 1 ] && ok "identify_rom base FireRed" || bad "identify base"
[ "$(result_text 63 | jq -r '.isCleanDump' 2>/dev/null)" = "false" ] && ok "identify_rom detects edit" || bad "identify edited"
[ "$(result_text 63 | jq -r '.baseGame' 2>/dev/null | grep -ci 'FireRed')" -ge 1 ] && ok "identify_rom base after edit (hack base)" || bad "identify base after edit"

echo "================="
echo "PASS=$PASS  FAIL=$FAIL"
[ "$FAIL" -eq 0 ] && echo "ALL GREEN" && exit 0 || exit 1
