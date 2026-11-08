#!/usr/bin/env bash
# Call the test server's API as admin:  e2e/api.sh GET /LocalPrerollsPro/Files
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TOKEN=$(cat "$ROOT/.jellyfin-test/token")
curl -sS -X "$1" "http://localhost:${JF_PORT:-8096}$2" -H "Authorization: MediaBrowser Client=\"e2e\", Device=\"e2e\", DeviceId=\"e2e\", Version=\"1\", Token=\"$TOKEN\"" -H "Content-Type: application/json" ${3:+-d "$3"}
