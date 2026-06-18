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

ROM="$(pwd)/test/roms/firered.gba"
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

echo "== 2-6. drive server =="
{
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":99}}}'; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"save_rom\",\"arguments\":{\"outPath\":\"$OR\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$OR\"}}}"; sleep 15
  printf '%s\n' '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 2
  # re-open original so exports reflect the clean ROM
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"open_rom\",\"arguments\":{\"path\":\"$R\"}}}"; sleep 15
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"export_table\",\"arguments\":{\"name\":\"data.pokemon.wild\",\"outPath\":\"$ENCW\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"export_table\",\"arguments\":{\"name\":\"data.trainers.stats\",\"outPath\":\"$TRNW\"}}}"; sleep 2
  printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"export_table\",\"arguments\":{\"name\":\"data.pokedex.stats\",\"outPath\":\"$DEXW\"}}}"; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":13,"method":"tools/call","params":{"name":"run_script","arguments":{"script":"","path":"resources/Scripts/Add Mechanics From Later Generations/AnyGame_PixilateStyleAbilities.hma"}}}'; sleep 8
  printf '%s\n' '{"jsonrpc":"2.0","id":14,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{}}}'; sleep 2
  printf '%s\n' '{"jsonrpc":"2.0","id":15,"method":"tools/call","params":{"name":"goto","arguments":{"target":"Pokemon"}}}'; sleep 2
} | "./$EXE" > "$OUT" 2>"$ERR"

echo "== assertions =="
# 2. protocol + tools/list
[ "$(jq -rs 'map(select(.id==2))[0].result.tools|length' "$OUT" 2>/dev/null)" = "10" ] && ok "tools/list shows 10 tools" || bad "tools/list"
# 3. open + read known value
[ "$(is_error 3)" = "false" ] && ok "open_rom" || bad "open_rom"
[ "$(result_text 4 | jq -r '.rows[0].hp' 2>/dev/null)" = "45" ] && ok "read_table Bulbasaur hp=45" || bad "read_table known value"
# 4. write + save + reload reflects change
[ "$(is_error 5)" = "false" ] && ok "write_value" || bad "write_value (TODO)"
[ "$(is_error 6)" = "false" ] && [ -f "$OUTROM" ] && ok "save_rom wrote file" || bad "save_rom (TODO)"
[ "$(result_text 8 | jq -r '.rows[0].hp' 2>/dev/null)" = "99" ] && ok "edit persisted (hp=99 after reload)" || bad "edit did not persist (TODO)"
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

echo "================="
echo "PASS=$PASS  FAIL=$FAIL"
[ "$FAIL" -eq 0 ] && echo "ALL GREEN" && exit 0 || exit 1
