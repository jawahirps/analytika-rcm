"""
Bix Pet — a tiny standalone watcher agent that lives OUTSIDE the Bix app.

Watches the full sync/parse pipeline on the live DB and shows it as a
little animated pet with live stats at  http://localhost:5177

Design rules:
  * Read-only DB access, and ONLY dirt-cheap probes (MAX(Id) primary-key
    lookups + one COUNT that SQLite answers from stats). Never a blob scan,
    never a heavy GROUP BY — the pet must never slow the app it watches.
  * All analysis is delta-based: growth of PortalTransactions.Id = sync
    activity; growth of XmlParsedRecords.Id = parse activity. Rates are
    computed per minute from the poll history.
  * If /healthz fails or writes look stuck, the pet gets visibly alarmed.

Run:  python bix_pet.py          (serves on port 5177, polls every 20s)
Stop: kill the process.
"""
import json
import os
import sqlite3
import threading
import time
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

DB = r"J:\GhafAnalytika\Analytika\analytika.db"
HEALTH = "http://127.0.0.1:5000/healthz"
PORT = 5177
POLL_SECONDS = 20
HISTORY_MAX = 360  # ~2 hours of polls

state_lock = threading.Lock()
state = {
    "started": time.time(),
    "history": [],   # list of {t, tx_max, parsed_max, tx_count, healthy, health_ms}
    "latest": None,
    "error": None,
}


def cheap_probe():
    """One poll: only instant queries (PK MAX lookups) + healthz ping."""
    row = {"t": time.time(), "healthy": False, "health_ms": None,
           "tx_max": None, "parsed_max": None, "tx_count": None,
           "parsed_recent_facility": None, "wal_mb": None}
    # WAL size — zero-cost write-activity signal. Downloads fill blobs on
    # EXISTING rows (no id/count movement), but every write lands in the WAL,
    # so cumulative WAL growth is the cheapest possible download detector.
    try:
        row["wal_mb"] = round(os.path.getsize(DB + "-wal") / 1e6, 2)
    except OSError:
        pass
    # health ping
    try:
        t0 = time.time()
        with urllib.request.urlopen(HEALTH, timeout=5) as r:
            row["healthy"] = (r.status == 200)
        row["health_ms"] = int((time.time() - t0) * 1000)
    except Exception:
        row["healthy"] = False
    # db probes — read-only, short busy timeout so we never queue behind writers
    try:
        con = sqlite3.connect(f"file:{DB}?mode=ro", uri=True, timeout=4)
        con.execute("PRAGMA busy_timeout=3000")
        row["tx_max"] = con.execute("SELECT MAX(Id) FROM PortalTransactions").fetchone()[0]
        row["parsed_max"] = con.execute("SELECT MAX(Id) FROM XmlParsedRecords").fetchone()[0]
        row["tx_count"] = con.execute("SELECT COUNT(*) FROM PortalTransactions").fetchone()[0]
        try:
            fid = con.execute(
                "SELECT FacilityId FROM XmlParsedRecords WHERE Id=(SELECT MAX(Id) FROM XmlParsedRecords)"
            ).fetchone()
            row["parsed_recent_facility"] = fid[0] if fid else None
        except Exception:
            pass
        con.close()
    except Exception as e:
        row["db_error"] = str(e)
    return row


def rate_per_min(history, key, window_s=600):
    """Growth of `key` per minute over the last `window_s` seconds."""
    now = time.time()
    pts = [h for h in history if h.get(key) is not None and now - h["t"] <= window_s]
    if len(pts) < 2:
        return 0.0
    dv = pts[-1][key] - pts[0][key]
    dt = pts[-1]["t"] - pts[0]["t"]
    return round(dv / dt * 60.0, 1) if dt > 0 else 0.0


def write_mb_per_min(history, window_s=300):
    """Positive WAL growth per minute. Sums only positive deltas because a
    checkpoint truncates the WAL (negative jump) without meaning 'no writes'."""
    now = time.time()
    pts = [h for h in history if h.get("wal_mb") is not None and now - h["t"] <= window_s]
    if len(pts) < 2:
        return 0.0
    grown = sum(max(b["wal_mb"] - a["wal_mb"], 0) for a, b in zip(pts, pts[1:]))
    dt = pts[-1]["t"] - pts[0]["t"]
    return round(grown / dt * 60.0, 2) if dt > 0 else 0.0


def analyze():
    """Turn the poll history into a pet mood + verdicts."""
    with state_lock:
        hist = list(state["history"])
    if not hist:
        return {"mood": "hatching", "face": "🥚", "line": "Bix Pet is hatching…",
                "verdicts": [], "stats": {}}
    last = hist[-1]
    sync_rate = rate_per_min(hist, "tx_count")     # new transactions found/min
    parse_rate = rate_per_min(hist, "parsed_max")  # new parsed rows/min
    write_rate = write_mb_per_min(hist)            # WAL growth MB/min (download detector)
    verdicts, stats = [], {
        "healthy": last.get("healthy"),
        "health_ms": last.get("health_ms"),
        "transactions": last.get("tx_count"),
        "parsed_row_high_water": last.get("parsed_max"),
        "sync_rate_per_min": sync_rate,
        "parse_rate_per_min": parse_rate,
        "write_mb_per_min": write_rate,
        "last_parsed_facility": last.get("parsed_recent_facility"),
        "watching_for_min": round((time.time() - state["started"]) / 60),
    }
    if not last.get("healthy"):
        return {"mood": "alarmed", "face": "🙀", "line": "ALERT — Bix is not answering /healthz!",
                "verdicts": ["App unreachable — restart BixApp if this persists."], "stats": stats}
    if "db_error" in last:
        verdicts.append(f"DB probe issue: {last['db_error'][:80]}")
    downloading = write_rate >= 0.5 and parse_rate == 0 and sync_rate == 0
    if sync_rate > 0 and parse_rate > 0:
        mood, face, line = "thrilled", "😸", f"Sync AND parse are flowing — {sync_rate:.0f} tx/min found, {parse_rate:.0f} rows/min parsed."
        verdicts.append("Full-stretch sync is being taken care of: fetch → download → parse all moving.")
    elif parse_rate > 0:
        mood, face, line = "munching", "😋", f"Munching XML — parsing {parse_rate:.0f} rows/min."
        verdicts.append("Parse pipeline active." + (f" Writes also flowing at {write_rate:.1f} MB/min." if write_rate >= 0.5 else ""))
    elif sync_rate > 0:
        mood, face, line = "fetching", "🐕", f"Fetching — {sync_rate:.0f} new transactions/min from the portal."
        verdicts.append("Sync is finding records; parse will follow (auto-parse after sync).")
    elif downloading:
        mood, face, line = "downloading", "📥", f"Downloading files — writes flowing at {write_rate:.1f} MB/min."
        verdicts.append("XML files are being downloaded into existing records (no new ids — blob fills). Parse comes after.")
    else:
        mood, face, line = "napping", "😴", "All quiet — no sync, download or parse running. zzz…"
        verdicts.append("Pipeline idle. Backlog: run a Sync (portal login) or the ParseAll tool to advance it.")
    if last.get("health_ms") and last["health_ms"] > 2000:
        verdicts.append(f"App is slow to answer ({last['health_ms']} ms) — heavy job running?")
    return {"mood": mood, "face": face, "line": line, "verdicts": verdicts, "stats": stats}


def poller():
    # Never let one bad poll kill the watcher — a dead poller silently freezes
    # the stats at their last values (the pet naps through real activity).
    while True:
        try:
            row = cheap_probe()
            with state_lock:
                state["history"].append(row)
                state["history"] = state["history"][-HISTORY_MAX:]
                state["latest"] = row
        except Exception as e:
            with state_lock:
                state["error"] = repr(e)
        try:
            time.sleep(POLL_SECONDS)
        except Exception:
            pass


PAGE = """<!doctype html><html><head><meta charset="utf-8">
<title>Bix Pet — sync watcher</title>
<style>
 body{margin:0;font-family:Inter,'Segoe UI',system-ui,sans-serif;color:#EAF4FB;min-height:100vh;
  background:radial-gradient(900px 500px at 80% -10%,rgba(84,172,191,.25),transparent 60%),
             radial-gradient(700px 500px at -10% 40%,rgba(0,104,132,.18),transparent 60%),
             linear-gradient(165deg,#04141F,#06202E 55%,#03121C);display:flex;align-items:center;justify-content:center}
 .card{width:min(560px,92vw);background:rgba(8,26,44,.72);border:1px solid rgba(122,190,210,.18);
  border-radius:22px;padding:28px 30px;backdrop-filter:blur(18px) saturate(160%);box-shadow:0 24px 60px rgba(0,0,0,.45)}
 .pet{font-size:84px;text-align:center;animation:bob 2.4s ease-in-out infinite;filter:drop-shadow(0 8px 18px rgba(84,172,191,.35))}
 @keyframes bob{0%,100%{transform:translateY(0)}50%{transform:translateY(-10px)}}
 .alarmed .pet{animation:shake .5s linear infinite}
 @keyframes shake{0%,100%{transform:translateX(0)}25%{transform:translateX(-6px)}75%{transform:translateX(6px)}}
 h1{font-size:15px;letter-spacing:.14em;text-transform:uppercase;color:#54ACBF;text-align:center;margin:6px 0 2px}
 .line{text-align:center;font-size:15px;margin:8px 0 18px;color:#EAF4FB}
 .grid{display:grid;grid-template-columns:1fr 1fr;gap:10px}
 .stat{background:rgba(4,16,31,.55);border:1px solid rgba(122,190,210,.12);border-radius:14px;padding:10px 14px}
 .stat b{display:block;font-size:20px;color:#A7EBF2}
 .stat span{font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:rgba(234,244,251,.55)}
 .verdicts{margin-top:16px;font-size:12.5px;color:rgba(234,244,251,.8)}
 .verdicts li{margin:4px 0}
 .foot{margin-top:14px;text-align:center;font-size:11px;color:rgba(234,244,251,.4)}
 .ok{color:#5EE0A0}.bad{color:#E2625A}
</style></head><body><div class="card" id="card">
 <div class="pet" id="face">🥚</div><h1>Bix Pet · sync watcher</h1>
 <div class="line" id="line">waking up…</div>
 <div class="grid" id="grid"></div>
 <ul class="verdicts" id="verdicts"></ul>
 <div class="foot" id="foot"></div>
</div><script>
async function tick(){
 try{
  const s=await (await fetch('/status')).json();
  document.getElementById('face').textContent=s.face;
  document.getElementById('line').textContent=s.line;
  document.getElementById('card').className='card '+s.mood;
  const st=s.stats||{};
  const fmt=v=>v==null?'—':Number(v).toLocaleString();
  document.getElementById('grid').innerHTML=
   `<div class="stat"><b class="${st.healthy?'ok':'bad'}">${st.healthy?'ONLINE':'DOWN'}</b><span>bix app (${st.health_ms??'—'} ms)</span></div>`+
   `<div class="stat"><b>${fmt(st.transactions)}</b><span>records found (total)</span></div>`+
   `<div class="stat"><b>${fmt(st.sync_rate_per_min)}/min</b><span>sync rate (new tx)</span></div>`+
   `<div class="stat"><b>${fmt(st.parse_rate_per_min)}/min</b><span>parse rate (rows)</span></div>`+
   `<div class="stat"><b>${fmt(st.write_mb_per_min)} MB/min</b><span>download / write flow</span></div>`+
   `<div class="stat"><b>${fmt(st.parsed_row_high_water)}</b><span>parsed rows (high water)</span></div>`;
  document.getElementById('verdicts').innerHTML=(s.verdicts||[]).map(v=>'<li>'+v+'</li>').join('');
  document.getElementById('foot').textContent='watching for '+(st.watching_for_min??0)+' min · polls DB every 20s (PK-only, zero-load probes)';
 }catch(e){
  document.getElementById('face').textContent='🙀';
  document.getElementById('line').textContent='Pet lost contact with its own watcher process!';
 }
}
tick();setInterval(tick,5000);
</script></body></html>"""


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):  # keep the console quiet
        pass

    def do_GET(self):
        if self.path.startswith("/status"):
            body = json.dumps(analyze()).encode()
            ctype = "application/json"
        else:
            body = PAGE.encode()
            ctype = "text/html; charset=utf-8"
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    threading.Thread(target=poller, daemon=True).start()
    print(f"Bix Pet watching at http://localhost:{PORT}  (db={DB})")
    ThreadingHTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
