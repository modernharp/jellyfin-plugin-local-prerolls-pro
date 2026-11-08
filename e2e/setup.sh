#!/usr/bin/env bash
# Builds the plugin and sets up a throwaway Jellyfin server with test media, all inside
# local-prerolls-pro/.jellyfin-test/ (git-ignored). Needs: .NET 10 SDK, ffmpeg, curl, python3.
#   e2e/setup.sh          full setup (downloads Jellyfin the first time)
#   e2e/setup.sh plugin   rebuild and reinstall only the plugin, then restart
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
T="$ROOT/.jellyfin-test"
M="$T/media"
PORT="${JF_PORT:-8096}"
PLUGIN_DIR="$T/data/plugins/LocalPrerollsPro_1.0.0.0"
mkdir -p "$T"

install_plugin() {
  dotnet publish "$ROOT/src/Jellyfin.Plugin.LocalPrerollsPro" -c Release -o "$T/build" --nologo -v q
  mkdir -p "$PLUGIN_DIR"
  cp "$T/build/Jellyfin.Plugin.LocalPrerollsPro.dll" "$PLUGIN_DIR/"
  echo "Plugin installed to $PLUGIN_DIR"
}

if [ "${1:-}" = "plugin" ]; then
  "$ROOT/e2e/jf.sh" stop || true
  install_plugin
  "$ROOT/e2e/jf.sh" start
  exit 0
fi

# 1. Download Jellyfin (latest stable, Linux x64) into the repo folder.
if [ ! -x "$T/server/jellyfin/jellyfin" ]; then
  BASE="https://repo.jellyfin.org/files/server/linux/latest-stable/amd64/"
  FILE=$(curl -sSL "$BASE" | grep -oE 'jellyfin_[0-9.]+-amd64\.tar\.gz' | head -1)
  echo "Downloading $FILE"
  mkdir -p "$T/server"
  curl -sSL -o "$T/server/jellyfin.tar.gz" "$BASE$FILE"
  tar -xzf "$T/server/jellyfin.tar.gz" -C "$T/server"
  rm "$T/server/jellyfin.tar.gz"
fi

# 2. Test media.
if [ ! -d "$M" ]; then
  echo "Making test media"
  mk() { # path size seconds [vcodec] [acodec] [extra audio filter]
    mkdir -p "$(dirname "$1")"
    ffmpeg -loglevel error -y -f lavfi -i "testsrc2=size=$2:rate=24" -f lavfi -i "sine=frequency=440:sample_rate=48000${6:-}" \
      -t "$3" -c:v "${4:-libx264}" -pix_fmt yuv420p -c:a "${5:-aac}" -shortest "$1"
  }
  movie() { # folder title year rating genre tag size
    local d="$M/movies/$1"; mk "$d/$1.mp4" "$7" 6
    printf '<?xml version="1.0" encoding="utf-8"?>\n<movie><title>%s</title><year>%s</year><premiered>%s-06-01</premiered><mpaa>%s</mpaa><genre>%s</genre><tag>%s</tag></movie>\n' "$2" "$3" "$3" "$4" "$5" "$6" > "$d/$1.nfo"
  }
  movie "Night Terror (2021)" "Night Terror" 2021 R Horror spooky 640x360
  movie "Kids Fun (2020)" "Kids Fun" 2020 G Animation family 640x360
  movie "Space Odyssey (2019)" "Space Odyssey" 2019 PG-13 "Science Fiction" space 3840x2160
  movie "No Intro Film (2018)" "No Intro Film" 2018 PG Drama no-preroll 640x360
  movie "Funny Day (1985)" "Funny Day" 1985 PG Comedy retro 640x360
  mk "$M/movies/Funny Day (1985)/Funny Day (1985)-trailer.mp4" 640x360 3
  mk "$M/movies/Night Terror (2021)/.prerolls/terror-welcome.mp4" 640x360 3
  mkdir -p "$M/tv/Test Show/Season 01"
  printf '<?xml version="1.0" encoding="utf-8"?>\n<tvshow><title>Test Show</title><genre>Drama</genre><mpaa>TV-14</mpaa></tvshow>\n' > "$M/tv/Test Show/tvshow.nfo"
  mk "$M/tv/Test Show/Season 01/Test Show S01E01.mp4" 640x360 6
  mk "$M/tv/Test Show/Season 01/Test Show S01E02.mp4" 640x360 6

  P="$M/prerolls"
  mk "$P/neon-city.mp4" 640x360 3
  mk "$P/retro_cinema.mp4" 640x360 3
  printf '{ "name": "Retro Cinema", "tags": ["retro"], "rating": "PG", "credit": "Test Studio" }\n' > "$P/retro_cinema.mp4.json"
  mk "$P/scary.mp4" 640x360 3
  printf '{ "tags": ["horror"], "rating": "R" }\n' > "$P/scary.json"
  mk "$P/hevc-ac3.mkv" 640x360 3 libx265 ac3
  mk "$P/too-loud.mp4" 640x360 4 libx264 aac ",volume=8"
  mk "$P/holidays/xmas.mp4" 640x360 3
  printf '{ "defaults": { "tags": ["christmas"], "from": "12-01", "to": "12-26" } }\n' > "$P/holidays/prerolls.json"
  echo "not a video" > "$P/notes.nfo"
fi

# 3. Build and install the plugin, start the server.
"$ROOT/e2e/jf.sh" stop >/dev/null 2>&1 || true
install_plugin

# Local Intros settings (improved-fork format) so the import can be tested.
mkdir -p "$T/data/plugins/configurations"
cat > "$T/data/plugins/configurations/Jellyfin.Plugin.LocalIntros.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<IntroPluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
  <SchemaVersion>2</SchemaVersion>
  <IntroFolders><string>$M/prerolls</string></IntroFolders>
  <ScanSubfolders>true</ScanSubfolders>
  <Rules>
    <IntroRule><Name>Horror</Name><Enabled>true</Enabled><Precedence>2</Precedence><Weight>1</Weight><MatchMode>All</MatchMode>
      <Intros><string>scary.mp4</string></Intros>
      <Conditions><RuleCondition><Kind>Genre</Kind><Values><string>Horror</string></Values><Negate>false</Negate></RuleCondition></Conditions></IntroRule>
    <IntroRule><Name>All intros</Name><Enabled>true</Enabled><Precedence>0</Precedence><Weight>1</Weight><MatchMode>All</MatchMode>
      <Intros><string>*</string></Intros><Conditions /></IntroRule>
  </Rules>
  <PreShow><PreShowStep><Kind>Rules</Kind><Enabled>true</Enabled><Count>1</Count></PreShowStep></PreShow>
  <EnabledItemTypes><string>Movie</string><string>Episode</string></EnabledItemTypes>
</IntroPluginConfiguration>
XML

"$ROOT/e2e/jf.sh" start

# 4. First-run wizard, admin user (admin/admin) and libraries.
api() { curl -sS -X "$1" "http://localhost:$PORT$2" -H "Authorization: MediaBrowser Client=\"e2e\", Device=\"e2e\", DeviceId=\"e2e\", Version=\"1\"${TOKEN:+, Token=\"$TOKEN\"}" -H "Content-Type: application/json" ${3:+-d "$3"}; }
TOKEN=""
if api GET /Startup/User >/dev/null 2>&1 && [ ! -f "$T/token" ]; then
  api POST /Startup/Configuration '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}'
  api GET /Startup/User >/dev/null
  api POST /Startup/User '{"Name":"admin","Password":"admin"}'
  api POST /Startup/Complete
  TOKEN=$(api POST /Users/AuthenticateByName '{"Username":"admin","Pw":"admin"}' | python3 -c 'import sys,json; print(json.loads(sys.stdin.read().lstrip("\ufeff"))["AccessToken"])')
  echo "$TOKEN" > "$T/token"
  api POST "/Library/VirtualFolders?name=Movies&collectionType=movies&refreshLibrary=false" "{\"LibraryOptions\":{\"PathInfos\":[{\"Path\":\"$M/movies\"}]}}"
  api POST "/Library/VirtualFolders?name=Shows&collectionType=tvshows&refreshLibrary=true" "{\"LibraryOptions\":{\"PathInfos\":[{\"Path\":\"$M/tv\"}]}}"
  echo "Waiting for the library scan"; sleep 15
fi
echo "Ready: http://localhost:$PORT  (admin / admin). Run: python3 e2e/api_test.py && node e2e/ui_test.js"
