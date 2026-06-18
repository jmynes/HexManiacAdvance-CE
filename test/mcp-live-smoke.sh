#!/usr/bin/env bash
# Live-mode gate: with a running GUI, the MCP server must target the open tab.
# Launches the GUI on the test ROM, waits for its automation pipe, then drives
# the MCP server and asserts every response is mode:"live" and a write round-trips.
# Requires a desktop session (the WPF GUI shows a window).
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
export PATH="/c/Program Files/dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

GUI="artifacts/HexManiac.WPF/bin/Release/net6.0-windows/HexManiacAdvance.exe"
MCP="artifacts/HexManiac.Mcp/bin/Release/net8.0/HexManiac.Mcp.exe"
GUIW="$(cygpath -w "$(pwd)/$GUI")"
ROMW="$(cygpath -w "$(pwd)/test/roms/firered.gba")"
TMP="test/.tmp"; mkdir -p "$TMP"; OUT="$TMP/live.jsonl"
PASS=0; FAIL=0
ok(){ echo "  PASS: $1"; PASS=$((PASS+1)); }
bad(){ echo "  FAIL: $1"; FAIL=$((FAIL+1)); }

echo "== build =="
taskkill //F //IM HexManiacAdvance.exe //IM HexManiac.Mcp.exe >/dev/null 2>&1 || true
dotnet build -c Release -p:PlatformTarget=x64 -v quiet >/dev/null 2>&1 || { echo "GUI build failed"; exit 1; }
( cd src/HexManiac.Mcp && dotnet build HexManiac.Mcp.csproj -c Release -v quiet ) >/dev/null 2>&1 || { echo "MCP build failed"; exit 1; }

echo "== launch GUI =="
powershell.exe -NoProfile -Command "Start-Process -FilePath '$GUIW' -ArgumentList '$ROMW','--skip-splash'" >/dev/null 2>&1

echo "== wait for automation pipe =="
ready=0
for i in $(seq 1 40); do
  if powershell.exe -NoProfile -Command "try { \$c=New-Object System.IO.Pipes.NamedPipeClientStream('.','HexManiacAdvance.Automation','InOut'); \$c.Connect(500); \$c.Dispose(); exit 0 } catch { exit 1 }" >/dev/null 2>&1; then ready=1; break; fi
  sleep 1
done
[ "$ready" = "1" ] && ok "pipe ready" || { bad "pipe never came up"; taskkill //F //IM HexManiacAdvance.exe >/dev/null 2>&1 || true; echo "PASS=$PASS FAIL=$FAIL"; exit 1; }

echo "== drive MCP =="
{
  printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"live","version":"1"}}}'
  printf '%s\n' '{"jsonrpc":"2.0","method":"notifications/initialized"}'
  printf '%s\n' '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_open_roms","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"write_value","arguments":{"table":"data.pokemon.stats","index":1,"field":"hp","value":77}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"read_table","arguments":{"name":"data.pokemon.stats","start":1,"count":1}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_shortcuts","arguments":{}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"goto","arguments":{"target":"Pokemon"}}}'; sleep 1
  printf '%s\n' '{"jsonrpc":"2.0","id":8,"method":"tools/call","params":{"name":"goto","arguments":{"target":"nonsense_xyz_no_such_target"}}}'; sleep 1
} | "./$MCP" > "$OUT" 2>/dev/null

rt(){ jq -rs --argjson id "$1" 'map(select(.id==$id))[0].result.content[0].text//empty' "$OUT"; }
echo "== assertions =="
[ "$(rt 2 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "list_open_roms mode=live" || bad "list_open_roms not live"
[ "$(rt 2 | jq -r '.tabs[0].file' 2>/dev/null | grep -ci firered)" -ge 1 ] && ok "firered tab listed" || bad "firered tab missing"
[ "$(rt 3 | jq -r '.rows[0].hp' 2>/dev/null)" = "45" ] && ok "live read hp=45" || bad "live read"
[ "$(rt 4 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "write_value mode=live" || bad "write not live"
[ "$(rt 4 | jq -r '.newValue' 2>/dev/null)" = "77" ] && ok "write_value newValue=77" || bad "write newValue"
[ "$(rt 5 | jq -r '.rows[0].hp' 2>/dev/null)" = "77" ] && ok "live write read-back hp=77" || bad "write not reflected"
[ "$(rt 6 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "list_shortcuts mode=live" || bad "list_shortcuts not live"
[ "$(rt 6 | jq -r '.shortcuts[].display' 2>/dev/null | grep -ci '^Pokemon$')" -ge 1 ] && ok "Pokemon shortcut listed" || bad "Pokemon shortcut missing"
[ "$(rt 7 | jq -r '.mode' 2>/dev/null)" = "live" ] && ok "goto mode=live" || bad "goto not live"
[ "$(rt 7 | jq -r '.ok' 2>/dev/null)" = "true" ] && ok "goto ok=true" || bad "goto not ok"
[ -n "$(rt 7 | jq -r '.resolved // empty' 2>/dev/null)" ] && ok "goto resolved non-empty" || bad "goto resolved empty"
[ "$(rt 8 | jq -r '.ok // "false"' 2>/dev/null)" != "true" ] && ok "goto rejects unknown target" || bad "goto did not reject unknown target"
[ -n "$(rt 8 | jq -r '.error // empty' 2>/dev/null)" ] && ok "goto unknown-target error message" || bad "goto unknown-target error message"

taskkill //F //IM HexManiacAdvance.exe >/dev/null 2>&1 || true
echo "================="
echo "PASS=$PASS  FAIL=$FAIL"
[ "$FAIL" -eq 0 ] && echo "LIVE GREEN" && exit 0 || exit 1
