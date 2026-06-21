# Regenerates the output/_work/*.json inputs that build_dump.py consumes, by driving the
# HexManiacAdvance MCP server (the built exe) over stdio JSON-RPC. Run this first, then build_dump.py.
#
#   python scripts/firered-pokedump/export_inputs.py
#   python scripts/firered-pokedump/build_dump.py
#
# Needs the MCP exe built (dotnet build src/HexManiac.Mcp -c Release) and the FireRed ROM at
# test/roms/FireRed-netcompare.gba. Launching a second MCP instance alongside a running one is fine
# (each is its own headless session); only rebuilding the locked exe would conflict.
import json, os, subprocess, threading, queue, time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXE  = os.path.join(REPO, "artifacts", "HexManiac.Mcp", "bin", "Release", "net6.0", "HexManiac.Mcp.exe")
ROM  = os.path.join(REPO, "test", "roms", "FireRed-netcompare.gba")
WORK = os.path.join(REPO, "output", "_work")
os.makedirs(WORK, exist_ok=True)

# output/_work/<file>.json  <-  export_table <anchor>   (species-indexed tables keep internal ids; placeholders dropped)
TABLES = {
    "names": "data.pokemon.names",      "stats": "data.pokemon.stats",
    "types": "data.pokemon.type.names", "abilities": "data.abilities.names",
    "movenames": "data.pokemon.moves.names", "items": "data.items.stats",
    "levelup": "data.pokemon.moves.levelup", "tms": "data.pokemon.moves.tms",
    "tutors": "data.pokemon.moves.tutors",   "evolutions": "data.pokemon.evolutions",
    "trades": "data.pokemon.trades",    "wild": "data.pokemon.wild",
    "mapnames": "data.maps.names",
}
# output/_work/<file>.json  <-  dedicated export tool
TOOLS = {
    "coin-prizes": "export_coin_prizes",     "script-encounters": "export_script_encounters",
    "species-sources": "export_species_sources", "pokedex": "export_pokedex",
}

proc = subprocess.Popen([EXE], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
q = queue.Queue(); threading.Thread(target=lambda: [q.put(l) for l in proc.stdout], daemon=True).start()
_id = [0]
def send(o): proc.stdin.write(json.dumps(o) + "\n"); proc.stdin.flush()
def rpc(method, params=None, t=180):
    _id[0] += 1; rid = _id[0]
    send({"jsonrpc": "2.0", "id": rid, "method": method, "params": params or {}})
    end = time.time() + t
    while time.time() < end:
        try: line = q.get(timeout=end - time.time())
        except queue.Empty: break
        if line.strip() and ('"id": %d' % rid in line or '"id":%d' % rid in line):
            try: return json.loads(line)
            except Exception: pass
    return None
def call(name, args):
    r = rpc("tools/call", {"name": name, "arguments": args})
    txt = r["result"]["content"][0]["text"] if r and "result" in r else '{"ERR":"no response"}'
    return json.loads(txt)

rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "inputs", "version": "1"}})
send({"jsonrpc": "2.0", "method": "notifications/initialized"})
print("open_rom:", call("open_rom", {"path": ROM}).get("ok"))
for fname, anchor in TABLES.items():
    r = call("export_table", {"name": anchor, "outPath": os.path.join(WORK, fname + ".json")})
    print(f"  export_table {anchor:32} -> {fname}.json  ({r.get('rowCount', r.get('ok'))})")
for fname, tool in TOOLS.items():
    r = call(tool, {"outPath": os.path.join(WORK, fname + ".json")})
    print(f"  {tool:32} -> {fname}.json  ({r.get('ok')})")
proc.stdin.close()
try: proc.wait(timeout=8)
except Exception: proc.terminate()
print("done -> output/_work/")
