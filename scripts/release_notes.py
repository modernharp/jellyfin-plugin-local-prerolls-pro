"""Release notes from CHANGELOG.md.
   python3 scripts/release_notes.py 1.0.0.0 owner/repo v1.0.0   -> Markdown for the GitHub release page."""
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def changelog_for(version):
    """The bullet list under '## [x.y.z]' in CHANGELOG.md for this version (4-part or 3-part)."""
    text = open(os.path.join(ROOT, "CHANGELOG.md"), encoding="utf-8").read()
    short = re.sub(r"(\.0)+$", "", version) or version
    for v in (version, short, ".".join(version.split(".")[:3])):
        m = re.search(r"^## \[" + re.escape(v) + r"\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.S | re.M)
        if m:
            return m.group(1).strip()
    return "See CHANGELOG.md."


if __name__ == "__main__":
    version, repo, tag = sys.argv[1], sys.argv[2], sys.argv[3]
    raw = f"https://raw.githubusercontent.com/{repo}/main/manifest.json"
    print(f"""## Download

**[⬇ Download Local Pre-Rolls Pro {version}](https://github.com/{repo}/releases/download/{tag}/local-prerolls-pro_{version}.zip)** for Jellyfin 12.

## Install (easiest way, with automatic updates)

1. In Jellyfin, open **Dashboard → Plugins → Repositories** (or **Manage Repositories**) and click **+**.
2. Name: `Local Pre-Rolls Pro`. URL:
   ```
   {raw}
   ```
3. Save, open the **Catalog**, find **Local Pre-Rolls Pro**, click **Install**, then restart Jellyfin.

Prefer to install the zip by hand? See the [install guide](https://github.com/{repo}/blob/main/docs/INSTALL.md).

## What's new

{changelog_for(version)}
""")
