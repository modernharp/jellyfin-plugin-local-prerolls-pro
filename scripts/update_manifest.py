"""Adds a release to manifest.json, the plugin repository file Jellyfin reads.
   python3 scripts/update_manifest.py --version 1.0.0.0 --url <zip url> --checksum <md5> --repo owner/name [--branch main]"""
import argparse
import datetime
import json
import os
import sys

here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, here)
from release_notes import changelog_for  # noqa: E402

p = argparse.ArgumentParser()
p.add_argument("--version", required=True)
p.add_argument("--url", required=True)
p.add_argument("--checksum", required=True)
p.add_argument("--repo", required=True)
p.add_argument("--branch", default="main")
p.add_argument("--image-url", default=None)
p.add_argument("--manifest", default=os.path.join(os.path.dirname(here), "manifest.json"))
a = p.parse_args()

info = json.load(open(os.path.join(here, "plugin.json")))
try:
    manifest = json.load(open(a.manifest))
except (FileNotFoundError, json.JSONDecodeError):
    manifest = []

plugin = next((x for x in manifest if x.get("guid") == info["guid"]), None)
if plugin is None:
    plugin = {"guid": info["guid"], "versions": []}
    manifest.append(plugin)
plugin.update({
    "name": info["name"],
    "description": info["description"],
    "overview": info["overview"],
    "owner": info["owner"],
    "category": info["category"],
    "imageUrl": a.image_url or f"https://raw.githubusercontent.com/{a.repo}/{a.branch}/images/logo.png",
})

entry = {
    "version": a.version,
    "changelog": changelog_for(a.version),
    "targetAbi": info["targetAbi"],
    "sourceUrl": a.url,
    "checksum": a.checksum,
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
}
plugin["versions"] = [v for v in plugin.get("versions", []) if v.get("version") != a.version] + [entry]
plugin["versions"].sort(key=lambda v: [int(x) for x in v["version"].split(".")], reverse=True)

with open(a.manifest, "w") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")
print(f"manifest.json now lists {len(plugin['versions'])} version(s); newest {plugin['versions'][0]['version']}")
