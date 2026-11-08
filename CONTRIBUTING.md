# Contributing

Thanks for helping! Bug reports, ideas and pull requests are all welcome.

## Build

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet test                      # unit tests
scripts/package.sh 1.0.0         # builds dist/local-prerolls-pro_1.0.0.0.zip
```

## Try it on a real Jellyfin server

`e2e/setup.sh` downloads Jellyfin into the git-ignored `.jellyfin-test/` folder, makes test videos with ffmpeg, installs the plugin and starts the server on http://localhost:8096 (admin / admin). You also need ffmpeg, Python 3 and Node.js with Playwright.

```bash
e2e/setup.sh                 # first time: full setup
e2e/setup.sh plugin          # after a code change: rebuild and reinstall the plugin
python3 e2e/api_test.py      # live API checks
node e2e/ui_test.js          # browser checks including real playback
node e2e/ui_full_test.js     # clicks through every control on the settings page
e2e/jf.sh stop
```

The API and full UI tests expect a fresh server: delete `.jellyfin-test/{data,config,cache,log,token,media}` and run `e2e/setup.sh` again between runs.

## Code layout

| Folder | What's in it |
|---|---|
| `src/.../Engine` | Pure logic (rule matching, selection, dates). Unit-tested without Jellyfin. |
| `src/.../Services` | Scanning, history, conversion, the prerolls.me catalog, the pre-show builder. |
| `src/.../API` | Endpoints used by the settings page. |
| `src/.../Configuration` | Settings, their validation and the settings page (`config.html`). |
| `tests` | Unit tests. |
| `e2e` | Live tests against a real Jellyfin server. |

## Pull requests

- Keep changes focused, and add or update tests.
- Text people see should be short, plain and say what to do next.
- Be gentle with prerolls.me: no extra requests in loops; use the cached catalog.
