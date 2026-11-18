"""Archive the public content of the live VDarts servers (vdarts.net).

Only unauthenticated, read-only GET/POST calls that the game itself makes
without a member token. Sequential, rate limited, resumable (files already
saved are skipped). Writes manifest.jsonl with URL, status, size, sha256.

usage: python -I archive_vdarts.py <out_dir> [--delay 0.6]
"""
import hashlib
import html.parser
import json
import os
import re
import ssl
import sys
import time
import urllib.parse
import urllib.request

API = "https://api.vdarts.net/MemberAPI"
API8000 = "http://api.vdarts.net:8000/MemberAPI"
API8043 = "https://api.vdarts.net:8043/MemberAPI"
JAVA = "http://api.vdarts.net:8080/VDartsAPIs"
LANGS = ["en-us", "zh-tw", "zh-cn", "fr-fr", "es-es", "ja-jp", "ko-kr"]
RANKING_GAMES = {"202": "501", "208": "online-501", "301": "cricket", "308": "online-cricket",
                 "101": "countup", "102": "online-countup", "603": "sniper", "604": "online-sniper"}
UA = "UnityPlayer/2018.4.36f1 (UnityWebRequest/1.0, libcurl/7.52.0-DEV)"

ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE  # the cabinet never validated certificates either

out = sys.argv[1]
delay = float(sys.argv[sys.argv.index("--delay") + 1]) if "--delay" in sys.argv else 0.6
os.makedirs(out, exist_ok=True)
manifest = open(os.path.join(out, "manifest.jsonl"), "a", encoding="utf-8")
stats = {"ok": 0, "skip": 0, "fail": 0, "bytes": 0}


def fetch(url, rel, data=None, binary=False, must_image=False):
    """Download url to out/rel. Returns bytes (or None)."""
    path = os.path.join(out, rel)
    if os.path.exists(path) and os.path.getsize(path) > 0:
        stats["skip"] += 1
        with open(path, "rb") as f:
            return f.read()
    os.makedirs(os.path.dirname(path), exist_ok=True)
    body = None
    rec = {"url": url, "file": rel.replace("\\", "/"), "method": "POST" if data else "GET", "post": data,
           "time": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    for attempt in range(3):
        try:
            req = urllib.request.Request(url, data=data.encode() if data else None, headers={"User-Agent": UA})
            with urllib.request.urlopen(req, timeout=60, context=ctx) as r:
                body = r.read()
                rec["status"] = r.status
                rec["content_type"] = r.headers.get("Content-Type")
            break
        except urllib.error.HTTPError as e:
            rec["status"] = e.code
            rec["content_type"] = e.headers.get("Content-Type")
            body = None
            break
        except Exception as e:  # network hiccup: back off and retry
            rec["error"] = repr(e)
            time.sleep(5 * (attempt + 1))
    time.sleep(delay)
    if body is None or (must_image and not (rec.get("content_type") or "").startswith("image")):
        stats["fail"] += 1
        manifest.write(json.dumps(rec, ensure_ascii=False) + "\n")
        manifest.flush()
        return None
    with open(path, "wb") as f:
        f.write(body)
    rec["size"] = len(body)
    rec["sha256"] = hashlib.sha256(body).hexdigest()
    rec.pop("error", None)
    stats["ok"] += 1
    stats["bytes"] += len(body)
    manifest.write(json.dumps(rec, ensure_ascii=False) + "\n")
    manifest.flush()
    return body


def saved(rel):
    path = os.path.join(out, rel)
    if os.path.exists(path):
        with open(path, "rb") as f:
            return f.read()
    return None


def jload(b):
    if not b:
        return None
    try:
        return json.loads(b.decode("utf-8-sig"))
    except Exception:
        return None


def log(msg):
    print(f"[{time.strftime('%H:%M:%S')}] {msg}  ok={stats['ok']} skip={stats['skip']} fail={stats['fail']} "
          f"{stats['bytes']/1e6:.1f}MB", flush=True)


# ---- 1. API catalogs / listings ------------------------------------------------
log("catalogs")
fetch(f"{API}/echo.php", "api/echo.json")
fetch(f"{API}/fxGetAllBak.php", "api/fxGetAllBak.json")
for lang in LANGS:
    fetch(f"{API}/fxPersonal.php?type=all&language={lang}&price_json=1", f"api/fxPersonal_all_{lang}.json")
    fetch(f"{API}/fxGetAllBak2.php", f"api/fxGetAllBak2_{lang}.json",
          data=f"is_show_price=t&language={lang}&price_json=1")
    fetch(f"{API8000}/messageGet.php?lang={lang}&source=11", f"api/messageGet_{lang}.json")
fetch(f"{API}/leagueTeamRank.php", "api/leagueTeamRank.json")
fetch(f"{API}/leagueMatchList.php?mac=", "api/leagueMatchList.json")
fetch(f"{API}/gamePriceGet.php?mac=", "api/gamePriceGet.json")
fetch(f"{API}/gameRoundGet.php", "api/gameRoundGet.json", data="mac=")
for t in ("bet", "fee"):
    fetch(f"{API}/vpointBet.php?type={t}", f"api/vpointBet_{t}.json")
page = 1
while page <= 200:
    b = fetch(f"{API}/vpointBet.php?type=ranking&page={page}", f"api/vpointBet_ranking/page_{page:03d}.json")
    j = jload(b) if b else None
    if not j or not j.get("data"):
        break
    page += 1
fetch(f"{API}/league3MatchList.php?type=match&token=", "api/league3MatchList.json")
fetch(f"{API}/league4Match.php?requesttype=get_match_list&page=1&type=4&token=", "api/league4Match_list.json")
fetch(f"{API}/supervMachineSchedule.php?mac=", "api/supervMachineSchedule.json")
fetch("http://ftp.vdarts.net/download/vdarts-u3d.tar.gz.php", "api/update_vdarts-u3d.json")

log("java api")
for code, name in RANKING_GAMES.items():
    fetch(f"{JAVA}/gameRanking?game={code}", f"java/gameRanking_{code}_{name}.json")
fetch(f"{JAVA}/version?machine_type=1&version_type=now", "java/version.json")

# ---- 2. Effect backgrounds (game + menu), both formats the client uses ---------
bak = jload(fetch(f"{API}/fxGetAllBak.php", "api/fxGetAllBak.json")) or {}
log(f"backgrounds: {len(bak)} items")
for i, (key, uid) in enumerate(sorted(bak.items())):
    for ext in ("jpg", "png"):
        fetch(f"{API8043}/download/fx/bak/{uid}.{ext}", f"fx/bak/{key}__{uid}.{ext}", must_image=True)
    if i % 25 == 0:
        log(f"  bak {i}/{len(bak)}")

# ---- 3. Effect previews (videos / sounds) from every language catalog ---------
items = {}
for lang in LANGS:
    for it in jload(saved(f"api/fxPersonal_all_{lang}.json")) or []:
        if isinstance(it, dict) and it.get("url"):
            items[it["url"]] = it.get("i18n_index", "x")
log(f"effect previews: {len(items)} items")
for uid, idx in sorted(items.items(), key=lambda kv: kv[1]):
    fetch(f"{API8043}/download/fx/video/{uid}.jpg", f"fx/video/{idx}__{uid}.jpg", must_image=True)

# ---- 4. Announcement media ------------------------------------------------------
log("announcement media")
for lang in LANGS:
    j = jload(saved(f"api/messageGet_{lang}.json")) or {}
    base = j.get("media_path") or ""
    for msg in j.get("data") or []:
        for m in msg.get("media_data") or []:
            for k in ("thumbnail", "url"):
                v = m.get(k)
                if v:
                    full = v if v.startswith("http") else base + v
                    name = re.sub(r"[^A-Za-z0-9._-]", "_", urllib.parse.urlparse(full).path.lstrip("/"))
                    fetch(full, f"announcements/{name}")

# ---- 5. League team logos -------------------------------------------------------
j = jload(saved("api/leagueTeamRank.json")) or {}
for season in (j.get("list") or []) if isinstance(j, dict) else []:
    for r in season.get("rankings") or []:
        logo = r.get("logo")
        if logo:
            fetch(f"http://tw.vdarts.net:8000/MemberAPI/download/team/{logo}", f"team/{logo}")


# ---- 6. Public website (same host, depth-limited) -------------------------------
class Links(html.parser.HTMLParser):
    def __init__(self):
        super().__init__()
        self.links = []

    def handle_starttag(self, tag, attrs):
        for k, v in attrs:
            if k in ("href", "src") and v:
                self.links.append(v)


def site(root, max_pages=400, max_depth=3):
    host = urllib.parse.urlparse(root).netloc
    seen, queue, pages = set(), [(root, 0)], 0
    while queue and pages < max_pages:
        url, depth = queue.pop(0)
        # hrefs on the site carry spaces/newlines; percent-encode them instead of failing.
        url = urllib.parse.quote(urllib.parse.urldefrag(url.strip())[0], safe=":/?&=%+@,;~!$'()*")
        if url in seen:
            continue
        seen.add(url)
        p = urllib.parse.urlparse(url)
        # "/news" and "/news/x" both exist: store extensionless pages as folders so a page never blocks a directory.
        path = urllib.parse.unquote(p.path)  # save "Count Up.jpg", not "Count%20Up.jpg", so saved pages' links work
        last = path.rsplit("/", 1)[-1]
        rel = path if "." in last else path.rstrip("/") + "/index.html"
        if p.query:
            rel += "__" + re.sub(r"[^A-Za-z0-9=._-]", "_", p.query)
        b = fetch(url, os.path.join("site", host, rel.lstrip("/")))
        pages += 1
        if not b or depth >= max_depth or not re.search(rb"<html", b[:2000], re.I):
            continue
        lp = Links()
        try:
            lp.feed(b.decode("utf-8", "replace"))
        except Exception:
            continue
        for l in lp.links:
            u = urllib.parse.urljoin(url, l)
            if urllib.parse.urlparse(u).netloc == host and u.startswith("http"):
                queue.append((u, depth + 1))
    log(f"site {host}: {pages} files")


site("https://www.vdarts.net/")
site("https://api.vdarts.net/", max_pages=60, max_depth=1)

log("DONE")
