#!/usr/bin/env python3
"""Live tests for Local Pre-Rolls Pro against the server set up by e2e/setup.sh.

Run:  python3 e2e/api_test.py
Each check prints PASS or FAIL; the exit code is the number of failures.
Needs internet access to www.prerolls.me for the catalog checks (skipped if unreachable).
"""
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
T = os.path.join(ROOT, ".jellyfin-test")
BASE = "http://localhost:%s" % os.environ.get("JF_PORT", "8096")
TOKEN = open(os.path.join(T, "token")).read().strip()
PLUGIN = "d18a3ff6-9bbb-4218-9158-c9d2642eee86"
AUTH = 'MediaBrowser Client="e2e", Device="e2e", DeviceId="e2e", Version="1", Token="%s"' % TOKEN
failures = []


def call(method, path, body=None, raw=False):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method, headers={"Authorization": AUTH, "Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=120) as r:
            content = r.read()
            if raw:
                return r.status, r.headers.get("Content-Type"), content
            text = content.decode("utf-8-sig")
            return json.loads(text) if text.strip() else None
    except urllib.error.HTTPError as e:
        raise RuntimeError("%s %s -> HTTP %s: %s" % (method, path, e.code, e.read()[:300]))


def check(name, ok, detail=""):
    print(("PASS  " if ok else "FAIL  ") + name + ("" if ok else "   -> " + str(detail)))
    if not ok:
        failures.append(name)


def config():
    return call("GET", "/Plugins/%s/Configuration" % PLUGIN)


def save(c, wait=4):
    call("POST", "/Plugins/%s/Configuration" % PLUGIN, c)
    time.sleep(wait)  # The plugin rescans about 2 s after a save.


def files():
    return {f["Key"]: f for f in call("GET", "/LocalPrerollsPro/Files")["Files"]}


def item_id(name):
    r = call("GET", "/Items?Recursive=true&SearchTerm=%s&IncludeItemTypes=Movie,Episode" % urllib.parse.quote(name))
    return r["Items"][0]["Id"]


def simulate(name, at="2026-11-08T20:00", user=True):
    q = {"itemId": item_id(name), "at": at}
    if user:
        q["userId"] = USER
    return call("GET", "/LocalPrerollsPro/Simulate?" + urllib.parse.urlencode(q))


def names(sim):
    return [i["Name"] for i in sim.get("Intros", [])]


def wait_prepared(timeout=180):
    end = time.time() + timeout
    while time.time() < end:
        fs = files()
        if fs and all(f["PrepStatus"] in ("Original", "Ready", "Failed") for f in fs.values()):
            return fs
        time.sleep(3)
    return files()


USER = call("GET", "/Users")[0]["Id"]

print("== Import from Local Intros")
status = call("GET", "/LocalPrerollsPro/Status")
check("Local Intros settings are detected", status["Import"]["Available"], status)
res = call("POST", "/LocalPrerollsPro/Import")
c = config()
check("Import brings over folders and rules", len(c["IntroFolders"]) == 1 and [r["Name"] for r in c["Rules"]] == ["Horror", "All intros"], (c["IntroFolders"], [r["Name"] for r in c["Rules"]]))

print("== Scanning and per-file settings")
fs = files()
check("Six pre-rolls found, junk files ignored", sorted(fs) == sorted(["neon-city.mp4", "retro_cinema.mp4", "scary.mp4", "hevc-ac3.mkv", "too-loud.mp4", "holidays/xmas.mp4"]), sorted(fs))
r = fs.get("retro_cinema.mp4", {})
check("Sidecar file sets name, tags, rating, credit", r.get("Name") == "Retro Cinema" and r.get("Tags") == ["retro"] and r.get("Rating") == "PG" and r.get("Credit") == "Test Studio", r)
check("<name>.json sidecar works too", fs.get("scary.mp4", {}).get("Rating") == "R", fs.get("scary.mp4"))
x = fs.get("holidays/xmas.mp4", {})
check("Folder prerolls.json defaults apply", x.get("Tags") == ["christmas"] and x.get("From") == "12-01" and x.get("To") == "12-26", x)
check("New pre-rolls are flagged for the boost", all(f["IsNew"] for f in fs.values()))

print("== Conversion and loudness (background ffmpeg)")
fs = wait_prepared()
h = fs.get("hevc-ac3.mkv", {})
check("HEVC/AC3 pre-roll gets a converted copy", h.get("PrepStatus") == "Ready" and "converted" in (h.get("PreparedFor") or ""), h)
check("Normal pre-rolls are left as they are", fs["neon-city.mp4"]["PrepStatus"] == "Original", fs["neon-city.mp4"])
loud = fs.get("too-loud.mp4", {})
check("Loudness is measured", all(f.get("Loudness") is not None for f in fs.values()), {k: f.get("Loudness") for k, f in fs.items()})
check("Too-loud pre-roll gets a warning", any("louder" in n for n in loud.get("Notes", [])), loud.get("Notes"))

print("== Rules, age-safe, per-title and opt-outs")
sim = simulate("Night Terror")
check("Title's own .prerolls folder replaces rule picks", names(sim) == ["terror welcome"] and sim["Intros"][0]["Source"] == "Title", sim.get("Intros"))
c = config()
c["TitleIntrosMode"] = "AddFirst"
save(c)
sim = simulate("Night Terror")
check("'Add first' plays the title pre-roll, then the Horror rule pick", names(sim)[:1] == ["terror welcome"] and "scary" in names(sim), names(sim))

sim = simulate("Kids Fun")
excluded = {e["Name"]: e["Reason"] for e in sim.get("Excluded", [])}
check("Age-safe: R and PG pre-rolls left out before a G movie", "scary" in excluded and "Retro Cinema" in excluded, excluded)
check("Age-safe: an unrated pre-roll still plays before the G movie", len(names(sim)) == 1 and names(sim)[0] not in ("scary", "Retro Cinema"), names(sim))

sim = simulate("No Intro Film")
check("Opt-out tag 'no-preroll' skips the title", "no-preroll" in (sim.get("SkipReason") or ""), sim.get("SkipReason"))
c = config()
c["OptOutItemIds"] = [item_id("Funny Day")]
save(c)
sim = simulate("Funny Day")
check("No-pre-roll list skips the title", "never get pre-rolls" in (sim.get("SkipReason") or ""), sim.get("SkipReason"))

sim = simulate("Space Odyssey", at="2026-11-08T20:00")
excluded = {e["Name"]: e["Reason"] for e in sim.get("Excluded", [])}
check("Scheduled pre-roll is left out outside its dates", "xmas" in excluded, excluded)
c = config()
c["Rules"].append({"Name": "Christmas", "Enabled": True, "Precedence": 10, "Weight": 1, "MatchMode": "All", "Intros": ["tag:christmas"],
                   "Conditions": [{"Kind": "CurrentDate", "Values": [], "From": "12-01", "To": "12-26", "Negate": False}]})
c["FileSettings"] = [{"Key": "neon-city.mp4", "Tags": ["neon"], "Enabled": False}]
save(c)
sim = simulate("Space Odyssey", at="2026-12-24T20:00")
check("Rule targeting 'tag:christmas' picks the holiday pre-roll on Dec 24", names(sim) == ["xmas"], names(sim))
fs = files()
check("Settings-page file settings are applied (tag added, turned off)", fs["neon-city.mp4"]["Tags"] == ["neon"] and fs["neon-city.mp4"]["Enabled"] is False, fs["neon-city.mp4"])
sim = simulate("Space Odyssey", at="2026-11-08T20:00")
excluded = {e["Name"]: e["Reason"] for e in sim.get("Excluded", [])}
check("Turned-off pre-roll is never picked", excluded.get("neon city") == "turned off", excluded)

print("== Prepared copies are what Jellyfin streams")
c = config()
c["PreShow"] = [{"Kind": "Fixed", "Enabled": True, "Count": 1, "Intros": ["hevc-ac3.mkv"], "ApplyTo": [], "Group": "", "SameGenre": True}]
c["OptOutItemIds"] = []
save(c)
intros = call("GET", "/Items/%s/Intros?userId=%s" % (item_id("Space Odyssey"), USER))["Items"]
check("Jellyfin's intro API returns one pre-roll", len(intros) == 1, [i.get("Name") for i in intros])
info = call("POST", "/Items/%s/PlaybackInfo?userId=%s" % (intros[0]["Id"], USER), {}) if intros else {}
src = (info.get("MediaSources") or [{}])[0]
check("It streams the converted H.264/AAC MP4 copy", src.get("Path", "").endswith(".mp4") and "local-prerolls-pro" in src.get("Path", "")
      and {s.get("Codec") for s in src.get("MediaStreams", [])} >= {"h264", "aac"}, (src.get("Path"), [s.get("Codec") for s in src.get("MediaStreams", [])]))

print("== Volume levelling")
c = config()
c["LevelLoudness"] = True
save(c)
call("POST", "/LocalPrerollsPro/Prepare")
time.sleep(3)
fs = wait_prepared()
loud = fs["too-loud.mp4"]
check("Too-loud pre-roll gets a volume-matched copy", loud["PrepStatus"] == "Ready" and "volume-matched" in (loud.get("PreparedFor") or ""), loud)
copies = [os.path.join(dp, f) for dp, _, fn in os.walk(os.path.join(T, "cache", "local-prerolls-pro")) for f in fn if f.endswith(".mp4")]
measured = []
for path in copies:
    out = subprocess.run(["ffmpeg", "-hide_banner", "-nostdin", "-i", path, "-af", "loudnorm=print_format=json", "-f", "null", "-"], capture_output=True, text=True).stderr
    if '"input_i"' in out:
        measured.append(float(out.split('"input_i" : "')[1].split('"')[0]))
check("A copy measures close to -24 LUFS", any(abs(m + 24) < 2.5 for m in measured), (loud.get("Loudness"), measured))

print("== prerolls.me catalog")
try:
    cat = call("GET", "/LocalPrerollsPro/Catalog?platform=jellyfin&downloadableOnly=true&limit=100")
except RuntimeError as e:
    cat = None
    print("SKIP  catalog checks: " + str(e))
if cat:
    check("Catalog lists downloadable Jellyfin pre-rolls", cat["Total"] > 0 and all(i["IsDownloadable"] for i in cat["Items"]), cat["Total"])
    allcat = call("GET", "/LocalPrerollsPro/Catalog?limit=5")
    check("Full catalog includes YouTube/Vimeo items as links", allcat["Total"] > cat["Total"], (allcat["Total"], cat["Total"]))
    first = cat["Items"][0]
    status_code, ctype, body = call("GET", "/LocalPrerollsPro/Catalog/Thumb/%s" % first["Id"], raw=True)
    check("Thumbnails are served through the plugin", status_code == 200 and ctype.startswith("image/") and len(body) > 500, (status_code, ctype, len(body)))
    d = call("POST", "/LocalPrerollsPro/Catalog/Download/%s" % first["Slug"])
    fs = files()
    key = d.get("Key")
    check("Download saves the pre-roll into the folder and scans it", d.get("Ok") and key in fs, d)
    if key in fs:
        f = fs[key]
        check("Downloaded pre-roll is auto-tagged and credited", "prerolls.me" in f["Tags"] and first["Category"] in f["Tags"] and f.get("SourceUrl") == first["Url"], f)
    again = call("GET", "/LocalPrerollsPro/Catalog?platform=jellyfin&downloadableOnly=true&limit=100")
    check("Catalog marks it as downloaded", any(i["Slug"] == first["Slug"] and i["OnDisk"] for i in again["Items"]))

    cats = {}
    for i in cat["Items"]:
        cats[i["CategorySlug"]] = cats.get(i["CategorySlug"], 0) + 1
    small = sorted((n, s) for s, n in cats.items() if s != first["CategorySlug"])[0][1]
    c = config()
    c["CatalogSubscriptions"] = [{"Kind": "category", "Value": small, "Platform": "jellyfin", "Enabled": True}]
    save(c)
    s1 = call("POST", "/LocalPrerollsPro/Catalog/Sync")
    check("Subscription sync downloads the category's pre-rolls", s1["Downloaded"] == cats[small] and not s1["Failed"], (small, cats[small], s1))
    s2 = call("POST", "/LocalPrerollsPro/Catalog/Sync")
    check("Second sync downloads nothing new", s2["Downloaded"] == 0, s2)
    folder = os.path.join(T, "media", "prerolls", small)
    victim = sorted(p for p in os.listdir(folder) if p.endswith(".mp4"))[0]
    os.remove(os.path.join(folder, victim))
    s3 = call("POST", "/LocalPrerollsPro/Catalog/Sync")
    check("A deleted download is not downloaded again", s3["Downloaded"] == 0 and not os.path.exists(os.path.join(folder, victim)), s3)

print("== Rebuild keeps everything")
before = len(config()["Rules"])
call("POST", "/LocalPrerollsPro/Rebuild")
time.sleep(2)
check("Rules survive a rebuild", len(config()["Rules"]) == before)
intros = call("GET", "/Items/%s/Intros?userId=%s" % (item_id("Space Odyssey"), USER))["Items"]
check("Pre-rolls still play after a rebuild", len(intros) == 1, intros)

print()
print("%d failed" % len(failures) if failures else "All checks passed")
sys.exit(len(failures))
