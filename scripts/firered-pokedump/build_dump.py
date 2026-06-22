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

# CANONICAL national dex number per species, from the stored national-pokedex resource (PokeAPI order,
# bundled with the MCP). The ROM's own data.pokedex.* tables are sort/pointer tables, NOT a clean
# species->number map, and a species' INTERNAL index != its national dex for Hoenn - hence this lookup.
NAT={}
try:
    _natres=os.path.join(REPO,"src","HexManiac.Mcp","resources","national-pokedex.json")
    NAT={slug:i+1 for i,slug in enumerate(json.load(open(_natres,encoding="utf-8"))["order"])}
except Exception as ex: print("WARN: no national-pokedex resource:",ex)

# ---- assemble ----
stats=J("stats")["rows"]
out=[]
for rank,r in enumerate(stats,1):   # rank = internal-index order (NOT national dex for Hoenn)
    sp=r["index"]                   # internal species id (0-411)
    natdex=NAT.get(SLUG[sp], rank)  # canonical national dex from data.pokedex.national (fallback: rank)
    types=[TYPES[r["type1"]]]
    if r["type2"]!=r["type1"]: types.append(TYPES[r["type2"]])
    ab=[ABIL[r["ability1"]]]
    if r["ability2"]: ab.append(ABIL[r["ability2"]])
    held=[ITEM[i] for i in (r["item1"],r["item2"]) if i]
    bs={"hp":r["hp"],"attack":r["attack"],"defense":r["def"],"spAttack":r["spatk"],
        "spDefense":r["spdef"],"speed":r["speed"]}
    bs["total"]=sum(bs.values())
    sprec={
      "nationalDex":natdex, "speciesId":sp, "romOrder":rank, "name":NAME[sp], "slug":SLUG[sp],
      "pokedex":DEX.get(natdex),
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
            by_id[s].setdefault("evolvesFrom",[]).append({"from":NAME.get(sp,sp),"method":evo_desc(m,a),"methodCode":m,"requiresTrade":m in (5,6)})
for p in out:
    p.setdefault("evolvesFrom",[])

# Altering Cave's wild table lists 8 Johto species, but in every RELEASED FRLG the slot only ever yields
# ZUBAT - the alternates needed an e-Reader card that shipped only in Japan, so they're unreachable here.
EVENT_LOCKED_MAPS={"ALTERING CAVE"}
def direct_methods(p):   # sources that exist on their own, NOT depending on another species
    kinds={s["kind"] for s in p["scriptObtains"]}
    m=[]
    if any(w["map"] not in EVENT_LOCKED_MAPS for w in p["wildLocations"]): m.append("wild")
    if "gift" in kinds:    m.append("gift")
    if "static" in kinds:  m.append("static")
    if "legendary" in kinds: m.append("legendary")  # ticket legendaries detected via StartLegendaryBattle
    if "roaming" in kinds:   m.append("roaming")    # roamer detected via InitRoamer (CFRU-style hacks)
    if "starter" in kinds: m.append("starter")
    if "egg" in kinds:     m.append("egg")
    if p["trades"]:        m.append("trade")         # in-game NPC trade (no link cable needed)
    return m
# NOTE: "evolution" is intentionally NOT seeded here. The evolutions table carries data for the WHOLE
# national dex (incl. Hoenn/Johto lines absent from FireRed), so flagging every species-with-a-pre-evo
# as obtainable falsely lit up entire lines that aren't in the game - and then cascaded into "breeding".
# Evolution is instead added transitively below, only when a pre-evolution is itself reachable.
byname={p["name"]:p for p in out}
for p in out:
    p["obtainFromRomData"]=direct_methods(p)
    p["obtainCurated"]=[]

# ---- ROM-derived roaming beasts --------------------------------------------------------------------
# Vanilla FRLG picks the roaming legendary in an ASM starter-switch (no setvar), so the script walk
# can't read it. Follow the InitRoamer special's handler pointer (gSpecials[idx]) and read the species
# straight out of the `movs rX, #species` switch in that code. Returns [] off BPRE0 or if not found.
GSPECIALS_BPRE0=0x15FD60   # scripts.commands.events.specials (gSpecials), FireRed (BPRE0)
INITROAMER_SPECIAL=297     # index of the InitRoamer special in gSpecials (BPRE0)
def asm_roamer_species():
    if rom[0xAC:0xB0]!=b"BPRE": return []                  # constants are FireRed(BPRE0)-specific
    handler=ptr(GSPECIALS_BPRE0+INITROAMER_SPECIAL*4)
    if handler is None or handler+0x60>len(rom): return []
    handler&=~1                                             # clear the Thumb bit so instruction reads stay 2-byte aligned
    byreg={}                                                # reg -> [(addr, speciesId)] of movs species loads
    a=max(0,handler-0x140)&~1
    while a<handler+0x60:
        hw=u16(a)
        if 0x2000<=hw<0x2800:                              # Thumb: movs rR, #imm
            reg,imm=(hw>>8)&7, hw&0xFF
            if NAME.get(imm): byreg.setdefault(reg,[]).append((a,imm))   # imm is a real species id
        a+=2
    # the starter switch loads ONE register with the most distinct species in a tight cluster
    best=[]
    for lst in byreg.values():
        sp=sorted({s for _,s in lst})
        if len(sp)>=2 and (lst[-1][0]-lst[0][0])<=0x18 and len(sp)>len(best): best=sp
    return best

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
    # roamers: read the beast trio from the ASM starter-switch (ROM-derived). CFRU-style hacks instead
    # load it via setvar (already kind=roaming from the walk). Only if BOTH come up empty do we fall
    # back to the curated override.
    roamer_ids=asm_roamer_species()
    walk_roamers=[p["name"] for p in out if any(s["kind"]=="roaming" for s in p["scriptObtains"])]
    if roamer_ids:
        for sid in roamer_ids:
            q=byname.get(NAME[sid])
            if not q or any(s["kind"]=="roaming" for s in q["scriptObtains"]): continue
            q["scriptObtains"].append({"kind":"roaming","pickOne":True,"source":"asm:InitRoamer switch"})
            if "roaming" not in q["obtainFromRomData"]: q["obtainFromRomData"].append("roaming")
        roamer_src="ROM ASM (InitRoamer handler switch)"
    elif walk_roamers:
        roamer_src="script walk (setvar before InitRoamer)"
    else:
        for n in OV["roaming"]["pickOneOf"]:
            add_cur(n,{"kind":"roaming","pickOne":True,"note":OV["roaming"]["note"]})
        roamer_src="curated override (ASM scan + walk both empty)"
    print("roaming beasts from:",roamer_src,"->",
          [NAME[s] for s in roamer_ids] or walk_roamers or OV["roaming"]["pickOneOf"])
    # ticket legendaries: Lugia/Deoxys are auto-detected (kind=legendary); only curate the ones the walk misses (Ho-Oh)
    for e in OV["eventTicket"]["entries"]:
        q=byname.get(e["species"])
        if q and any(s["kind"]=="legendary" for s in q["scriptObtains"]): continue
        add_cur(e["species"],{"kind":"event","location":e["location"],"ticket":e["ticket"]})
except Exception as ex:
    print("WARN: no overrides:",ex)

# ============================ OBTAINABILITY CLOSURE (drives all four tiers) ============================
# direct_methods + curated overrides above seeded the genuine "starting point" sources. Everything else
# is reached transitively. One closure answers every question by toggling two switches:
#   allow_trade_evo : may trade-evolutions (Kadabra->Alakazam, etc.) fire?  (only once a link cable is in play)
#   HAS_DAY_NIGHT   : does the cart have a clock?  Vanilla FRLG does NOT, so Day/Night-friendship evolutions
#                     (Eevee->Espeon/Umbreon) never trigger here.  <-- per user: "day/night evolutions are out"
EGG_UNBREEDABLE={"Undiscovered","Ditto"}
EVO_DEAD_IN_FRLG={2,3}            # high-friendship Day / Night
EVO_TRADE={5,6}                  # trade / trade-with-item
EVENT_ONLY={"MEW","CELEBI","JIRACHI"}   # never a normal in-game encounter anywhere in Gen 3 (distribution-only)
EVENT_GATED={"LUGIA","HO-OH","DEOXYS"}  # battleable IN FRLG, but only after an event-distributed ticket (Mystic/Aurora)
HAS_DAY_NIGHT=False              # flip True for a romhack that adds a real-time clock

def direct_seeds():   # species that stand on their own - NOT reached through another species
    return {p["name"] for p in out
            if [m for m in p["obtainFromRomData"] if m not in ("evolution","breeding")] or p["obtainCurated"]}

def reach(seeds, allow_trade_evo, tag=False):
    """Close `seeds` under evolution (respecting FRLG mechanics) + breeding-with-Ditto, to a fixpoint.
    tag=True also stamps 'evolution'/'breeding' onto each species' obtainFromRomData (used once, for the
    canonical per-mon labels). Returns the set of reachable species names."""
    R=set(seeds)
    def line_in(p,seen):
        if p["name"] in seen: return False
        seen.add(p["name"])
        if p["name"] in R: return True
        return any(line_in(byname[e["into"]],seen) for e in p.get("evolvesInto",[]) if e["into"] in byname)
    growing=True
    while growing:   # loop both rules together: a bred baby (Tyrogue) can unlock a further evolution (Hitmontop)
        growing=False
        for p in out:                                                # EVOLUTION
            if p["name"] in R: continue
            for ev in p["evolvesFrom"]:
                mc=ev.get("methodCode")
                if mc in EVO_DEAD_IN_FRLG and not HAS_DAY_NIGHT: continue
                if mc in EVO_TRADE and not allow_trade_evo: continue
                if ev["from"] in R:
                    R.add(p["name"]); growing=True
                    if tag and "evolution" not in p["obtainFromRomData"]: p["obtainFromRomData"].append("evolution")
                    break
        for p in out:                                                # BREEDING (base-form baby <- obtainable adult line)
            if p["name"] in R or p["evolvesFrom"] or not p.get("evolvesInto"): continue
            for e in p["evolvesInto"]:
                a=byname.get(e["into"])
                if a and line_in(a,set()) and not any(g in EGG_UNBREEDABLE for g in a["eggGroups"]):
                    R.add(p["name"]); growing=True
                    if tag and "breeding" not in p["obtainFromRomData"]: p["obtainFromRomData"].append("breeding")
                    break
    return R

direct=direct_seeds()
# (3) link-trade between two FireRed carts: trade-evos fire. This is also the canonical per-mon labelling pass.
uniq_fr_trade=reach(direct, allow_trade_evo=True, tag=True)
# (1) any number of saves, NO link cable: trade-evos out (Day/Night already out).
uniq_no_trade=reach(direct, allow_trade_evo=False)
for p in out: p["obtainableWithoutTrade"]=p["name"] in uniq_no_trade
unobtainable=[p["name"] for p in out if not (p["obtainFromRomData"] or p["obtainCurated"])]
no_spawn=[p["name"] for p in out if not p["wildLocations"]]
obtainable_count=len(uniq_fr_trade)
without_trade=sorted(uniq_no_trade)

# ---- mutually-exclusive "pick one" choice groups -----------------------------------------------------
# oneSaveLock=True  -> picking one branch permanently forecloses the others in that save (shrinks tier 2).
# oneSaveLock=False -> a "soft" choice: breeding (or a stone) lets you still get the alternatives in one save.
starters=sorted(p["name"] for p in out if any(s["kind"]=="starter" for s in p["scriptObtains"]))
eeveelutions=sorted(p["name"] for p in out
                    if any(e["from"]=="EEVEE" for e in p["evolvesFrom"]) and p["name"] in uniq_no_trade)
choice_groups=[
  {"group":"starter","pickOneOf":starters,"oneSaveLock":True,"breedingRecovers":False,"derived":True,
   "from":"scripts.newgame.starters.{left,middle,right}",
   "note":"Exactly one starter line per playthrough; you cannot breed your way into the other two lines."},
  {"group":"mt-moon-fossil","pickOneOf":["KABUTO","OMANYTE"],"oneSaveLock":True,"breedingRecovers":False,"derived":False,
   "labels":{"KABUTO":"Dome Fossil","OMANYTE":"Helix Fossil"},
   "note":"One fossil ITEM is taken at Mt. Moon and revived at Cinnabar; the unchosen fossil's line can't be reached. AERODACTYL (Old Amber) is a separate, non-exclusive gift."},
  {"group":"fighting-dojo","pickOneOf":["HITMONLEE","HITMONCHAN"],"oneSaveLock":False,"breedingRecovers":True,"derived":False,
   "note":"The dojo gives ONE Hitmon, but breeding it with Ditto yields TYROGUE, which evolves into HITMONLEE / HITMONCHAN / HITMONTOP by its stats - so all of them are obtainable in a single save without trading."},
  {"group":"roaming-beast","pickOneOf":["RAIKOU","ENTEI","SUICUNE"],"oneSaveLock":True,"breedingRecovers":False,"derived":False,
   "note":"Post-National-Dex, exactly one legendary beast roams Kanto (chosen by your starter); legendaries can't be bred, so the other two are locked out of that save."},
]
if eeveelutions:
    choice_groups.append(
      {"group":"eevee-stone","pickOneOf":eeveelutions,"oneSaveLock":False,"breedingRecovers":True,"derived":True,
       "note":"One Eevee gift + one stone, but breeding Eevee with Ditto gives more Eevees, so every FRLG-valid Eeveelution is obtainable in one save. Espeon/Umbreon need Day/Night, which vanilla FRLG lacks."})
_gmap={m:g["group"] for g in choice_groups for m in g["pickOneOf"]}
for p in out:
    if p["name"] in _gmap: p["mutuallyExclusive"]=_gmap[p["name"]]

# ---- (2) one save file, NO trade: drop the unchosen branches of every HARD lock, then re-run the closure.
# Soft-lock alternatives (the Hitmons, the Eeveelutions) are recovered automatically by the breeding rule.
# Branches within a group are symmetric in size, so keeping the first of each is a representative best case.
hard_drop={n for g in choice_groups if g["oneSaveLock"] for n in g["pickOneOf"][1:]}
one_save=reach(direct - hard_drop, allow_trade_evo=False)
# A pure solo run (no trade AND no special events/distributions) also can't reach the ticket legendaries.
for p in out: p["requiresEvent"]=p["name"] in EVENT_GATED
solo_no_event=one_save-EVENT_GATED
# ---- (4) trade with OTHER Gen-3 versions: NOT derivable from the FireRed ROM (depends on LeafGreen / RSE /
# Colosseum-XD). Ceiling = every real species with a normal-gameplay source somewhere in Gen 3 - only the
# three event-only mythicals never qualify. (Espeon/Umbreon return via RSE's clock; LG exclusives via LeafGreen.)
cross_version=sorted(p["name"] for p in out if p["name"] not in EVENT_ONLY)
obtain_tiers={
  "1_uniqueNoTrade":len(uniq_no_trade),
  "2_oneSaveNoTrade":len(one_save),
  "2b_oneSaveNoTradeNoEvent":len(solo_no_event),
  "3_uniqueSameVersionTrade":len(uniq_fr_trade),
  "4_crossVersionTradeCeiling":len(cross_version),
}

doc={
  "rom":"Pokemon FireRed (USA) v1.0 (BPRE0)",
  "source":"HexManiacAdvance MCP table exports + ROM binary parse (net6)",
  "speciesCount":len(out),
  "fieldNotes":{
    "nationalDex":"CANONICAL National Pokedex number (1-386), from the stored national-pokedex resource (PokeAPI order, matched by slug). NOTE: a species' ROM order is NOT its national dex for Hoenn.",
    "speciesId":"Internal ROM species index - the species' actual position/id in the ROM's species-indexed tables (data.pokemon.*). Kanto/Johto are 1-251; the 25 limbo slots 252-276 are skipped; Hoenn is 277-411. e.g. TREECKO speciesId=277 but nationalDex=252.",
    "romOrder":"1-based sequential order of this species among the 386 REAL species as laid out in the ROM (speciesId with the limbo slots removed). Dense 1-386, no gaps; differs from nationalDex for Hoenn (TREECKO romOrder=252, WYNAUT romOrder=335 vs nationalDex 360).",
    "wildLocations":"grass/surf/rockSmash/fishing slots from data.pokemon.wild, with resolved map names; level ranges merged per map+method.",
    "trades":"in-game trade where this species is RECEIVED (data.pokemon.trades). NPC/location is script-based and not fully captured.",
    "heldItemsWild":"items held by wild members (data.pokemon.stats item1/item2).",
    "scriptObtains":"How the wild/evolution/trade tables don't cover obtaining, all AUTO-derived from the ROM: kind=gift (givePokemon: fossils, Eevee, Lapras, the Magikarp sale, the dojo Hitmon), static (setwildbattle: birds, Mewtwo, Snorlax), legendary (StartLegendaryBattle: Lugia/Ho-Oh/Deoxys ticket mons), starter (scripts.newgame.starters.* table), egg (giveEgg: Togepi), gameCorner (read from the prize multichoice), roaming (the beast trio, read from the InitRoamer ASM starter-switch). Nothing is curated in vanilla FRLG.",
    "obtainFromRomData":"Auto-derived methods: wild/gift/static/legendary/roaming/starter/egg/trade/gameCorner are direct sources; 'evolution' is added TRANSITIVELY (only when a pre-evolution is itself reachable and the evolution actually fires on a FireRed cart - Day/Night-friendship never does, so Espeon/Umbreon are out); 'breeding' = a base-form baby whose adult line is reachable, bred with Ditto.",
    "obtainCurated":"Methods that can't be read from the ROM, supplied by docs/firered-obtain-overrides.json as a FALLBACK only. In vanilla FireRed this is EMPTY for every species - even the roaming beasts are now read from the InitRoamer ASM switch. EMPTY obtainFromRomData AND obtainCurated = truly unobtainable in FireRed (gaps.unobtainableInFireRed).",
    "obtainableWithoutTrade":"true if reachable on a SINGLE cartridge - in-game NPC trades count, but trade-evolutions (Alakazam/Machamp/Golem/Gengar/Steelix/Scizor/Kingdra/Politoed/Slowking/Porygon2/...) and Day/Night friendship do not. This is the community 'single-player' count; evolvesFrom[].requiresTrade marks the trade-gated step.",
  },
  "choiceGroups":choice_groups,
  "obtainability":{
    "note":"Four tiers of access. Tiers 1-3 are derived from the FireRed ROM; tier 4 is a ceiling that depends on other Gen-3 games we can't read. Day/Night-friendship evolutions (Espeon/Umbreon) are excluded throughout because vanilla FRLG has no clock.",
    "uniqueNoTrade":{"count":len(uniq_no_trade),
       "desc":"Distinct species obtainable on a single FireRed with NO link cable, across any choices/replays. Trade-evolutions excluded."},
    "oneSaveNoTrade":{"count":len(one_save),
       "desc":"Most species obtainable in ONE save file, no trading - mutually-exclusive HARD locks (starter line, fossil, roaming beast) cost you their alternatives. Soft locks (Hitmons via Ditto->Tyrogue, Eeveelutions via breeding) are kept.",
       "lostToLocks":len(uniq_no_trade)-len(one_save),
       "hardLockGroups":[g["group"] for g in choice_groups if g["oneSaveLock"]]},
    "soloNoTradeNoEvent":{"count":len(solo_no_event),
       "desc":"oneSaveNoTrade minus the event-distribution legendaries (Lugia/Ho-Oh/Deoxys need a ticket from a Nintendo event). This is the strict 'solo, no trading, no events, no cheating' figure and matches the PokeCommunity completionist list (170).",
       "excludesEventGated":sorted(EVENT_GATED)},
    "uniqueSameVersionTrade":{"count":len(uniq_fr_trade),
       "desc":"Distinct species with FireRed<->FireRed link trading. Adds the trade-evolutions (Alakazam, Machamp, Golem, Gengar, Steelix, Scizor, Kingdra, Politoed, Porygon2, ...) on top of tier 1."},
    "crossVersionTradeCeiling":{"count":len(cross_version),
       "desc":"Theoretical National-Dex ceiling once you can trade with LeafGreen / Ruby-Sapphire-Emerald / Colosseum-XD: every real species except the 3 event-only mythicals (MEW/CELEBI/JIRACHI). NOT derivable from the FireRed ROM alone.",
       "excludes":sorted(EVENT_ONLY)},
  },
  "curatedOverrides":"docs/firered-obtain-overrides.json",
  "gaps":{
    "curatedResidue":"Vanilla FireRed is now FULLY ROM-derived - zero curated species. Auto-captured: the FIGHTING-DOJO Hitmons (givePokemon VAR from a one-time setvar), the GAME-CORNER prizes (prize multichoice via export_coin_prizes), the ticket legendaries Lugia/Ho-Oh/Deoxys (StartLegendaryBattle), and the roaming beasts RAIKOU/ENTEI/SUICUNE (read from the movs-species switch in the InitRoamer special's handler, since vanilla picks the roamer in ASM by starter with no setvar). docs/firered-obtain-overrides.json now only serves as a fallback if a romhack's ASM scan and script walk both come up empty.",
    "tradeNpcLocation":"data.pokemon.trades gives the received/offered species + NPC name, but WHICH map the trade NPC is on is script-based and not captured.",
    "alteringCave":"data.pokemon.wild lists 8 Johto species (Mareep, Aipom, Pineco, Shuckle, Teddiursa, Houndour, Stantler, Smeargle) in ALTERING CAVE, but every released FRLG locks that slot to ZUBAT; the alternates required a Japan-only e-Reader card. They are NOT counted as obtainable (their wildLocations entry is kept for ROM fidelity).",
    "obtainableCount":obtainable_count,
    "obtainableWithoutTradeCount":len(without_trade),
    "obtainableWithoutTrade":without_trade,
    "unobtainableInFireRed":unobtainable,
    "unobtainableInFireRedCount":len(unobtainable),
    "unobtainableNote":"unobtainableInFireRed = not reachable on a FireRed cart by ANY captured/curated method, even allowing link-trade between two Gen-3 carts (must be imported from another game or a real-world event): LeafGreen exclusives, most Johto/Hoenn species, and event-only MEW/CELEBI/JIRACHI. obtainableCount counts everything reachable (trade-evolutions included). obtainableWithoutTradeCount is the stricter single-cartridge figure (trade-evos and Day/Night friendship excluded), which lines up with community tallies (~171 for FireRed). The earlier build over-counted because every species with a pre-evolution was flagged obtainable, which also falsely seeded breeding across whole absent lines.",
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
print("--- obtainability tiers ---")
print("  1) unique, no trade            :",len(uniq_no_trade))
print("  2) one save file, no trade     :",len(one_save),f"  (-{len(uniq_no_trade)-len(one_save)} to hard locks: {[g['group'] for g in choice_groups if g['oneSaveLock']]})")
print("  2b) one save, no trade, no event:",len(solo_no_event),f"  (-{len(one_save)-len(solo_no_event)} ticket legendaries; == PokeCommunity solo list)")
print("  3) unique, same-version trade  :",len(uniq_fr_trade),f"  (+{len(uniq_fr_trade)-len(uniq_no_trade)} trade-evolutions)")
print("  4) cross-version ceiling       :",len(cross_version),f"  (386 real - {sorted(EVENT_ONLY)})")
print("unobtainable-in-FireRed (even with link-trade) count:",len(unobtainable))
print("unobtainable sample:",unobtainable[:30])
print("trades count:",sum(len(p['trades']) for p in out))
print("\nwrote",outpath, f"({os.path.getsize(outpath)//1024} KB)")
