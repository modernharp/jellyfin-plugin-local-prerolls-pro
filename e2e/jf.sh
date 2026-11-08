#!/usr/bin/env bash
# Start or stop the test Jellyfin server in .jellyfin-test/.
#   e2e/jf.sh start | stop | restart
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
T="$ROOT/.jellyfin-test"
PORT="${JF_PORT:-8096}"
case "${1:-}" in
  start)
    mkdir -p "$T/data" "$T/config" "$T/cache" "$T/log"
    FFMPEG="$(command -v ffmpeg || true)"
    nohup "$T/server/jellyfin/jellyfin" --datadir "$T/data" --configdir "$T/config" --cachedir "$T/cache" \
      --logdir "$T/log" --webdir "$T/server/jellyfin/jellyfin-web" ${FFMPEG:+--ffmpeg "$FFMPEG"} > "$T/stdout.log" 2>&1 &
    echo $! > "$T/pid"
    for i in $(seq 1 120); do
      # Wait for real JSON: during first-run setup Jellyfin serves a "starting up" HTML page.
      # Both must return JSON: during first-run setup most routes serve a "starting up" HTML page.
      if curl -s "http://localhost:$PORT/System/Info/Public" | python3 -c 'import sys,json; json.loads(sys.stdin.read().lstrip("\ufeff"))' 2>/dev/null \
         && curl -s "http://localhost:$PORT/Branding/Configuration" | python3 -c 'import sys,json; json.loads(sys.stdin.read().lstrip("\ufeff"))' 2>/dev/null; then
        echo "Jellyfin up on port $PORT after ${i}s"; exit 0
      fi
      sleep 1
    done
    echo "Jellyfin did not start"; tail -40 "$T/stdout.log"; exit 1 ;;
  stop)
    [ -f "$T/pid" ] || { echo "not running"; exit 0; }
    PID=$(cat "$T/pid"); kill "$PID" 2>/dev/null || true
    for i in $(seq 1 30); do kill -0 "$PID" 2>/dev/null || { echo stopped; rm -f "$T/pid"; exit 0; }; sleep 1; done
    kill -9 "$PID" 2>/dev/null || true; rm -f "$T/pid"; echo killed ;;
  restart) "$0" stop; "$0" start ;;
  *) echo "usage: $0 start|stop|restart"; exit 1 ;;
esac
