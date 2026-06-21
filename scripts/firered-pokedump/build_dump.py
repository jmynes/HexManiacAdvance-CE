# Builds output/firered-pokemon-full.json from the HexManiacAdvance MCP exports.
# Pipeline (see README.md): run the export_* MCP tools into output/_work/, then run this script.
import json, os, struct

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))  # scripts/firered-pokedump -> repo root
WORK = os.path.join(REPO, "output", "_work")                       # where the export_* MCP outputs live (gitignored)
ROM  = os.path.join(REPO, "test", "roms", "FireRed-netcompare.gba")
OVERRIDE = os.path.join(REPO, "docs", "firered-obtain-overrides.json")
OUTPATH  = os.path.join(REPO, "output", "firered-pokemon-full.json")
rom = open(ROM, "rb").read()

def J(n): return json.load(open(os.path.join(WORK, n+".json"), encoding="utf-8"))
def u8(o):  return rom[o]
def u16(o): return struct.unpack_from("<H", rom, o)[0]
def u32(o): return struct.unpack_from("<I", rom, o)[0]
def ptr(o):  # pointer read straight from ROM binary -> file offset
    v = u32(o)
    return (v - 0x08000000) if v >= 0x08000000 else (v if v else None)

# ---- Gen3 English PCS decode (enough for map names) ----
PCS = {0x00: " ", 0xAD: ".", 0xAE: "-", 0xBA: "/", 0xB8: ",", 0xAB: "!", 0xAC: "?",
       0xB1: '"', 0xB2: '"', 0xB3: "'", 0xB4: "'", 0x1B: "e"}
for i in range(10): PCS[0xA1+i] = chr(ord('0')+i)
for i in range(26): PCS[0xBB+i] = chr(ord('A')+i)
for i in range(26): PCS[0xD5+i] = chr(ord('a')+i)
def pcs(off):
    if not off: return None
    out = []
    while True:
        b = rom[off]; off += 1
        if b == 0xFF: break
        out.append(PCS.get(b, "?"))
    return "".join(out)

# ---- lookups ----
names   = {r["index"]: r["slug"].upper() if False else r for r in J("names")["rows"]}
NAME    = {r["index"]: r["name"] if "name" in r else r["slug"] for r in J("names")["rows"]}
SLUG    = {r["index"]: r["slug"] for r in J("names")["rows"]}
TYPES   = [r["name"] for r in J("types")["rows"]]
ABIL    = [r["name"] for r in J("abilities")["rows"]]
MOVE    = {r["index"]: r["name"] for r in J("movenames")["rows"]}
ITEM    = {r["index"]: r.get("name", f"item_{r['index']}") for r in J("items")["rows"]}
TMS     = [r["move"] for r in J("tms")["rows"]]      # 0..49 TM1-50, 50..57 HM1-8
TUTORS  = [r["move"] for r in J("tutors")["rows"]]   # 15
MAPNAME = [pcs(r["name"]) for r in J("mapnames")["rows"]]  # 109 region-section names

# ---- evolutions ----
def evo_desc(method, arg):
    I = ITEM.get(arg, f"item_{arg}")
    return {1:"High Friendship", 2:"Friendship (Day)", 3:"Friendship (Night)",
            4:f"Level {arg}", 5:"Trade", 6:f"Trade holding {I}", 7:f"Use {I}",
            8:f"Level {arg} (Atk>Def)", 9:f"Level {arg} (Atk=Def)", 10:f"Level {arg} (Atk<Def)",
            11:f"Level {arg} (Silcoon)", 12:f"Level {arg} (Cascoon)",
            13:f"Level {arg} (-> Ninjask)", 14:f"Level {arg} (-> Shedinja)",
            15:f"Level {arg} (high Beauty)"}.get(method, f"method{method} arg{arg}")
EVOS = {}   # sp -> [(method, arg, targetSp)]
for r in J("evolutions")["rows"]:
    lst = []
    for i in (1,2,3,4,5):
        m, a, s = r[f"method{i}"], r[f"arg{i}"], r[f"species{i}"]
        if m and s: lst.append((m, a, s))
    if lst: EVOS[r["index"]] = lst

# ---- script-granted encounters (gifts via givePokemon, statics via setwildbattle) ----
ENC = {}
try:
    _e = J("script-encounters")["bySpecies"]
    for k, lst in _e.items():
        ENC[int(k)] = [{"kind":s["kind"],"map":s["mapName"],"level":s["level"],
                        "heldItem":s["heldItem"],"scriptOffset":s["scriptOffset"]} for s in lst]
except Exception as ex:
    print("WARN: no script-encounters:", ex)

# ---- species cross-reference (HMA "Show Uses": table fields + script commands) ----
SRC = {}
try:
    _s = J("species-sources")["bySpecies"]
    for k, e in _s.items():
        sp = int(k); extra = []
        for t in e.get("tableRefs", []):
            if t["anchor"].startswith("scripts.newgame.starters"):
                slot = t["anchor"].split(".")[-1]  # left/middle/right
                extra.append({"kind":"starter","source":t["anchor"],"slot":slot})
        for r in e.get("scriptRefs", []):
            if r["command"] == "giveEgg":
                extra.append({"kind":"egg","map":r["mapName"],"scriptOffset":r["scriptOffset"]})
        if extra: SRC[sp] = extra
except Exception as ex:
    print("WARN: no species-sources:", ex)

GROWTH = ["Medium Fast", "Erratic", "Fluctuating", "Medium Slow", "Fast", "Slow"]
EGGGRP = {1:"Monster",2:"Water 1",3:"Bug",4:"Flying",5:"Field",6:"Fairy",7:"Grass",
          8:"Human-Like",9:"Water 3",10:"Mineral",11:"Amorphous",12:"Water 2",
          13:"Ditto",14:"Dragon",15:"Undiscovered"}
GENDER = {0:(100,0),31:(87.5,12.5),63:(75,25),127:(50,50),191:(25,75),225:(12.5,87.5),254:(0,100)}
STATKEY = ["hp","attack","defense","speed","spAttack","spDefense"]

def gender(g):
    if g == 255: return "genderless"
    if g in GENDER:
        m,f = GENDER[g]; return {"malePercent": m, "femalePercent": f}
    f = round(g/256*100,1); return {"malePercent": round(100-f,1), "femalePercent": f}

def evyield(ev):
    keys = ["hp","attack","defense","speed","spAttack","spDefense"]
    return {keys[i]: (ev>>(2*i))&3 for i in range(6) if (ev>>(2*i))&3}

# ---- moves: level-up (PLM), tm/hm + tutor (compat bitfields), egg ----
def plm(off):
    out=[]
    while True:
        v=u16(off); off+=2
        if v==0xFFFF: break
        move=v & 0x1FF; level=(v>>9)&0x7F
        out.append({"level":level,"move":MOVE.get(move,move)})
    return out
LVLPTR = {r["index"]: r["movesFromLevel"] for r in J("levelup")["rows"]}

TMCOMPAT_BASE=0x252BC8   # 8 bytes/species
TUTCOMPAT_BASE=0x459B7E  # 2 bytes/species
EGG_BASE=0x25EF0C
def tmhm(sp):
    base=TMCOMPAT_BASE+sp*8; out=[]
    for j in range(58):
        if rom[base+(j>>3)] & (1<<(j&7)):
            if j<50: out.append({"kind":"TM","number":j+1,"move":MOVE.get(TMS[j],TMS[j])})
            else:    out.append({"kind":"HM","number":j-49,"move":MOVE.get(TMS[j],TMS[j])})
    return out
def tutor(sp):
    base=TUTCOMPAT_BASE+sp*2; v=u16(base); out=[]
    for j in range(len(TUTORS)):
        if v & (1<<j): out.append(MOVE.get(TUTORS[j],TUTORS[j]))
    return out
# egg moves: flat u16 run; value>=20000 -> species marker; else move for current species
EGG={}
o=EGG_BASE; cur=None
while True:
    v=u16(o); o+=2
    if v==0xFFFF: break
    if v>=20000: cur=v-20000; EGG.setdefault(cur,[])
    elif cur is not None: EGG[cur].append(MOVE.get(v,v))

# ---- wild encounters + map name resolution ----
BANKS=0x3526A8
def mapname(bank,mp):
    blist=ptr(BANKS+bank*4)
    if blist is None: return f"bank{bank}-map{mp}"
    hdr=ptr(blist+mp*4)
    if hdr is None: return f"bank{bank}-map{mp}"
    rsid=u8(hdr+20)
    idx=rsid-88
    nm=MAPNAME[idx] if 0<=idx<len(MAPNAME) else None
    return nm or f"section{rsid}"

# species -> list of {map, method, minLevel, maxLevel}
spawns={}
def add_spawn(sp,mapnm,method,lo,hi):
    spawns.setdefault(sp,[])
    for e in spawns[sp]:
        if e["map"]==mapnm and e["method"]==method:
            e["minLevel"]=min(e["minLevel"],lo); e["maxLevel"]=max(e["maxLevel"],hi); return
    spawns[sp].append({"map":mapnm,"method":method,"minLevel":lo,"maxLevel":hi})

def slots(off,n):
    # encounter struct = {rate:u32, listPtr:u32 -> [low,high,species]*n}
    lp=u32(off+4)
    if lp<0x08000000: return []
    base=lp-0x08000000
    return [(u8(base+i*4),u8(base+i*4+1),u16(base+i*4+2)) for i in range(n)]
wild_unresolved=0
for w in J("wild")["rows"]:
    mn=mapname(w["bank"],w["map"])
    if mn.startswith("bank") or mn.startswith("section"): wild_unresolved+=1
    for key,method,cnt in [("grass","grass",12),("surf","surf",5),("tree","rockSmash",5)]:
        if w[key]:
            for lo,hi,sp in slots(w[key],cnt): add_spawn(sp,mn,method,lo,hi)
    if w["fish"]:
        fs=slots(w["fish"],10)
        for i,(lo,hi,sp) in enumerate(fs):
            rod = "oldRod" if i<2 else ("goodRod" if i<5 else "superRod")
            add_spawn(sp,mn,"fishing-"+rod,lo,hi)

# ---- trades (received species) ----
# trade NPC map locations (export_script_encounters tradeLocations, keyed by trade index)
TLOC={}
try: TLOC=J("script-encounters").get("tradeLocations",{})
except Exception: pass
NATURES=["Hardy","Lonely","Brave","Adamant","Naughty","Bold","Docile","Relaxed","Impish","Lax",
         "Timid","Hasty","Serious","Jolly","Naive","Modest","Mild","Quiet","Bashful","Rash",
         "Calm","Gentle","Sassy","Careful","Quirky"]
trades_by_recv={}
for ti,t in enumerate(J("trades")["rows"]):
    entry={"giveSpecies": NAME.get(t["give"],t["give"]), "receiveNickname": t["nickname"],
           "heldItem": (ITEM.get(t["helditem"]) if t["helditem"] else None),
           "nature": NATURES[t["personality"]%25],   # Gen-3 nature = personality % 25
           "ivs": {k:t[k] for k in ("hp","attack","defense","speed","spatk","spdef")},
           "otName": t["trainername"], "otId": t["trainerid"]}
    loc=TLOC.get(str(ti))
    if loc: entry["map"]=loc["mapName"]
    trades_by_recv.setdefault(t["receive"],[]).append(entry)

# pokedex flavor (export_pokedex), keyed by NATIONAL dex number
DEX={}
try: DEX={int(k):v for k,v in J("pokedex")["byNationalDex"].items()}
except Exception as ex: print("WARN: no pokedex:",ex)

# ---- assemble ----
stats=J("stats")["rows"]
out=[]
for rank,r in enumerate(stats,1):   # rank = national dex (stats are placeholder-excluded, in id order)
    sp=r["index"]                   # internal species id (0-411)
    types=[TYPES[r["type1"]]]
    if r["type2"]!=r["type1"]: types.append(TYPES[r["type2"]])
    ab=[ABIL[r["ability1"]]]
    if r["ability2"]: ab.append(ABIL[r["ability2"]])
    held=[ITEM[i] for i in (r["item1"],r["item2"]) if i]
    bs={"hp":r["hp"],"attack":r["attack"],"defense":r["def"],"spAttack":r["spatk"],
        "spDefense":r["spdef"],"speed":r["speed"]}
    bs["total"]=sum(bs.values())
    sprec={
      "nationalDex":rank, "speciesId":sp, "name":NAME[sp], "slug":SLUG[sp],
      "pokedex":DEX.get(rank),
      "types":types, "baseStats":bs, "abilities":ab,
      "catchRate":r["catchRate"], "baseExp":r["baseExp"],
      "evYield":evyield(r["evs"]), "genderRatio":gender(r["genderratio"]),
      "eggGroups":[EGGGRP.get(r["egg1"],r["egg1"])]+([EGGGRP.get(r["egg2"],r["egg2"])] if r["egg2"]!=r["egg1"] else []),
      "eggCycles":r["steps2hatch"], "baseFriendship":r["basehappiness"],
      "growthRate":GROWTH[r["growthrate"]] if r["growthrate"]<len(GROWTH) else r["growthrate"],
      "heldItemsWild":held,
      "moves":{
        "levelUp":plm(LVLPTR[sp]),
        "tmHm":tmhm(sp),
        "tutor":tutor(sp),
        "egg":EGG.get(sp,[]),
      },
      "wildLocations":sorted(spawns.get(sp,[]),key=lambda e:(e["map"],e["method"])),
      "scriptObtains":list(ENC.get(sp,[]))+SRC.get(sp,[]),
      "trades":trades_by_recv.get(sp,[]),
      "evolvesInto":[{"into":NAME.get(s,s),"method":evo_desc(m,a)} for (m,a,s) in EVOS.get(sp,[])],
    }
    out.append(sprec)

# ---- evolvesFrom (reverse) + obtainability gap analysis ----
by_id = {p["speciesId"]: p for p in out}
for (sp, lst) in EVOS.items():
    for (m,a,s) in lst:
        if s in by_id:
            by_id[s].setdefault("evolvesFrom",[]).append({"from":NAME.get(sp,sp),"method":evo_desc(m,a)})
for p in out:
    p.setdefault("evolvesFrom",[])

def derived_methods(p):   # obtain methods derived from tables + reachable scripts
    kinds={s["kind"] for s in p["scriptObtains"]}
    m=[]
    if p["wildLocations"]: m.append("wild")
    if "gift" in kinds:    m.append("gift")
    if "static" in kinds:  m.append("static")
    if "legendary" in kinds: m.append("legendary")  # ticket legendaries detected via StartLegendaryBattle
    if "roaming" in kinds:   m.append("roaming")    # roamer detected via InitRoamer (CFRU-style hacks)
    if "starter" in kinds: m.append("starter")
    if "egg" in kinds:     m.append("egg")
    if p["evolvesFrom"]:   m.append("evolution")
    if p["trades"]:        m.append("trade")
    return m
byname={p["name"]:p for p in out}
for p in out:
    p["obtainFromRomData"]=derived_methods(p)
    p["obtainCurated"]=[]

# ---- curated overrides: FRLG mechanisms that aren't in the script data (docs/firered-obtain-overrides.json) ----
try:
    OV=json.load(open(OVERRIDE,encoding="utf-8"))
    def add_cur(name,entry):
        p=byname.get(name)
        if not p: return
        p["scriptObtains"].append(entry)
        if entry["kind"] not in p["obtainCurated"]: p["obtainCurated"].append(entry["kind"])
    # Dojo Hitmons are now auto-detected (givePokemon VAR resolved from a one-time setvar); only curate as a fallback
    for n in OV["fightingDojo"]["pickOneOf"]:
        q=byname.get(n)
        if q and any(s["kind"]=="gift" for s in q["scriptObtains"]): continue
        add_cur(n,{"kind":"dojo","location":OV["fightingDojo"]["location"],"pickOne":True})
    # Game Corner prizes: read live from export_coin_prizes (the multichoice menu); fall back to the override list.
    try:    gc=J("coin-prizes")["prizes"]; gc_src="export_coin_prizes (scripts.text.multichoice)"
    except Exception: gc=OV["gameCorner"]["prizes"]; gc_src="curated override (export_coin_prizes output absent)"
    for pr in gc:  # gameCorner species+cost are READ from the ROM menu -> a derived (not curated) source
        q=byname.get(pr["species"])
        if not q: continue
        q["scriptObtains"].append({"kind":"gameCorner","location":OV["gameCorner"]["location"],"coins":pr["coins"]})
        if "gameCorner" not in q["obtainFromRomData"]: q["obtainFromRomData"].append("gameCorner")
    print("game corner prizes from:",gc_src,"->",[(pr["species"],pr["coins"]) for pr in gc])
    # roamers: detected on CFRU-style hacks (kind=roaming); vanilla picks the species in ASM, so curate as fallback
    for n in OV["roaming"]["pickOneOf"]:
        q=byname.get(n)
        if q and any(s["kind"]=="roaming" for s in q["scriptObtains"]): continue
        add_cur(n,{"kind":"roaming","pickOne":True,"note":OV["roaming"]["note"]})
    # ticket legendaries: Lugia/Deoxys are auto-detected (kind=legendary); only curate the ones the walk misses (Ho-Oh)
    for e in OV["eventTicket"]["entries"]:
        q=byname.get(e["species"])
        if q and any(s["kind"]=="legendary" for s in q["scriptObtains"]): continue
        add_cur(e["species"],{"kind":"event","location":e["location"],"ticket":e["ticket"]})
except Exception as ex:
    print("WARN: no overrides:",ex)

# ---- derive BREEDING: a base-form baby with a breedable egg group is obtainable by
# breeding the (obtainable) adult of its line with Ditto ----
EGG_UNBREEDABLE={"Undiscovered","Ditto"}
def obtainable(p): return bool(p["obtainFromRomData"] or p["obtainCurated"])
def line_obtainable(p,seen=None):
    seen=seen or set()
    if p["name"] in seen: return False
    seen.add(p["name"])
    if obtainable(p): return True
    return any(byname[e["into"]] is not None and line_obtainable(byname[e["into"]],seen)
               for e in p.get("evolvesInto",[]) if e["into"] in byname)
for p in out:
    if obtainable(p) or p["evolvesFrom"] or not p.get("evolvesInto"): continue
    # Gen-3 babies sit in the Undiscovered egg group themselves; you breed the ADULT (which
    # must be obtainable AND in a breedable egg group) with Ditto to get the baby egg.
    for e in p["evolvesInto"]:
        a=byname.get(e["into"])
        if a and line_obtainable(a) and not any(g in EGG_UNBREEDABLE for g in a["eggGroups"]):
            p["obtainFromRomData"].append("breeding"); break
unobtainable=[p["name"] for p in out if not (p["obtainFromRomData"] or p["obtainCurated"])]
no_spawn=[p["name"] for p in out if not p["wildLocations"]]

# ---- mutually-exclusive "pick one" choice groups ----
starters=sorted(p["name"] for p in out if any(s["kind"]=="starter" for s in p["scriptObtains"]))
choice_groups=[
  {"group":"starter","pickOneOf":starters,"derived":True,
   "from":"scripts.newgame.starters.{left,middle,right}"},
  {"group":"mt-moon-fossil","pickOneOf":["KABUTO","OMANYTE"],"derived":False,
   "labels":{"KABUTO":"Dome Fossil","OMANYTE":"Helix Fossil"},
   "note":"One fossil ITEM is taken at Mt. Moon and revived at Cinnabar; the choice is on the items, not the mon gift, so it isn't auto-derived. AERODACTYL (Old Amber) is a separate, non-exclusive gift."},
  {"group":"fighting-dojo","pickOneOf":["HITMONLEE","HITMONCHAN"],"derived":False,
   "note":"Curated (docs/firered-obtain-overrides.json): given by an ASM-hardcoded special at the Saffron Fighting Dojo - no literal species in the script data."},
  {"group":"roaming-beast","pickOneOf":["RAIKOU","ENTEI","SUICUNE"],"derived":False,
   "note":"Curated: post-National-Dex, exactly one legendary beast roams Kanto, determined by your starter."},
]
_gmap={m:g["group"] for g in choice_groups for m in g["pickOneOf"]}
for p in out:
    if p["name"] in _gmap: p["mutuallyExclusive"]=_gmap[p["name"]]

doc={
  "rom":"Pokemon FireRed (USA) v1.0 (BPRE0)",
  "source":"HexManiacAdvance MCP table exports + ROM binary parse (net6)",
  "speciesCount":len(out),
  "fieldNotes":{
    "wildLocations":"grass/surf/rockSmash/fishing slots from data.pokemon.wild, with resolved map names; level ranges merged per map+method.",
    "trades":"in-game trade where this species is RECEIVED (data.pokemon.trades). NPC/location is script-based and not fully captured.",
    "heldItemsWild":"items held by wild members (data.pokemon.stats item1/item2).",
    "scriptObtains":"How the wild/evolution/trade tables don't cover obtaining: kind=gift (givePokemon: fossils, Eevee, Lapras, the Magikarp sale), static (setwildbattle: birds, Mewtwo, Snorlax), starter (scripts.newgame.starters.* table), egg (giveEgg: Togepi) - all auto-derived; plus curated kinds dojo/gameCorner/roaming/event from docs/firered-obtain-overrides.json.",
    "obtainFromRomData":"Auto-derived methods: wild/gift/static/starter/egg/evolution/trade, plus 'breeding' (a base-form baby with a breedable egg group whose adult line is obtainable - bred with Ditto).",
    "obtainCurated":"Methods that can't be read from the ROM, supplied by docs/firered-obtain-overrides.json: dojo (Hitmons), gameCorner, roaming (beasts), event (ticket legendaries). EMPTY obtainFromRomData AND obtainCurated = truly unobtainable in FireRed (gaps.unobtainableInFireRed).",
  },
  "choiceGroups":choice_groups,
  "curatedOverrides":"docs/firered-obtain-overrides.json",
  "gaps":{
    "stillUncaptured":"Auto-extraction can't read two FRLG mechanisms (verified): the FIGHTING-DOJO Hitmons (species baked into an ASM 'special' - no literal givePokemon/setvar in the ROM) and the GAME-CORNER prizes (literal givePokemon, but in a menu jump-table script the top-level walk - and HMA's own 'Show Uses > scripts' - can't reach). These are now filled from the curated override file instead; roaming beasts and ticket legendaries likewise.",
    "tradeNpcLocation":"data.pokemon.trades gives the received/offered species + NPC name, but WHICH map the trade NPC is on is script-based and not captured.",
    "unobtainableInFireRed":unobtainable,
    "unobtainableInFireRedCount":len(unobtainable),
    "unobtainableNote":"Not obtainable in FireRed by any captured or curated method: LeafGreen version exclusives (Sandshrew, Vulpix, Bellsprout, Slowpoke, Staryu, Pinsir) and Johto/Hoenn species (trade-only after the National Dex), plus distribution-only events (MEW/CELEBI/JIRACHI). All require trading or a real-world distribution.",
  },
  "pokemon":out,
}
outpath=OUTPATH
json.dump(doc,open(outpath,"w",encoding="utf-8"),indent=2,ensure_ascii=False)

# ---- diagnostics ----
print("species:",len(out))
print("map names sample:", [m for m in MAPNAME[:6]])
b=out[0]; print("Bulbasaur types/ab:",b["types"],b["abilities"],"catch",b["catchRate"],"ev",b["evYield"],"gender",b["genderRatio"])
print("Bulbasaur levelUp[:4]:",b["moves"]["levelUp"][:4])
print("Bulbasaur tmHm[:3]:",b["moves"]["tmHm"][:3]," tutor:",b["moves"]["tutor"][:3]," egg[:3]:",b["moves"]["egg"][:3])
print("Bulbasaur spawns:",b["wildLocations"][:3])
print("wild map entries unresolved:",wild_unresolved,"/132")
print("species with NO wild spawn:",len(no_spawn))
print("Bulbasaur dex/id:",out[0]["nationalDex"],out[0]["speciesId"])
tre=next(p for p in out if p["name"]=="TREECKO"); print("Treecko dex/id:",tre["nationalDex"],tre["speciesId"])
for n in ["PORYGON","HITMONLEE","RAIKOU","LUGIA","PICHU","TYROGUE","MAGBY","MEW"]:
    p=next(x for x in out if x["name"]==n)
    print(f"  {n}: derived={p['obtainFromRomData']} curated={p['obtainCurated']} excl={p.get('mutuallyExclusive','-')}")
print("unobtainable-in-FireRed count:",len(unobtainable))
print("unobtainable sample:",unobtainable[:30])
print("trades count:",sum(len(p['trades']) for p in out))
print("\nwrote",outpath, f"({os.path.getsize(outpath)//1024} KB)")
