"""Turns a tag such as v1.2 or 1.2.3 into Jellyfin's four-part version 1.2.0.0 / 1.2.3.0."""
import re
import sys

raw = (sys.argv[1] if len(sys.argv) > 1 else "").strip()
m = re.fullmatch(r"v?(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:\.(\d+))?", raw)
if not m:
    sys.exit(f"'{raw}' is not a version. Use a tag like v1.0.0")
print(".".join(p or "0" for p in m.groups()))
