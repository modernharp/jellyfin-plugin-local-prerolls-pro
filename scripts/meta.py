"""Writes the meta.json that goes inside the plugin zip (used for manual installs)."""
import datetime
import json
import os
import sys

here = os.path.dirname(os.path.abspath(__file__))
info = json.load(open(os.path.join(here, "plugin.json")))
version = sys.argv[1]
sys.path.insert(0, here)
from release_notes import changelog_for  # noqa: E402

print(json.dumps({
    "category": info["category"],
    "changelog": changelog_for(version),
    "description": info["description"],
    "guid": info["guid"],
    "name": info["name"],
    "overview": info["overview"],
    "owner": info["owner"],
    "targetAbi": info["targetAbi"],
    "timestamp": datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "version": version,
    "status": "Active",
    "autoUpdate": True,
    "imagePath": "logo.png",
    "assemblies": [],
}, indent=2))
