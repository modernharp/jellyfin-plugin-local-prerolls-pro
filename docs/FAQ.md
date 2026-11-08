# Questions and answers

The plugin's **Help** tab has the same answers, right inside Jellyfin.

### Nothing plays before my movie

- Turn on **Cinema mode** in the app: **Settings → Playback → Cinema mode**. It's a setting per device and per app.
- Pre-rolls never play when you **resume** a video, or for the second and later items in a play queue.
- Some apps only ask for pre-rolls before **movies**, not episodes.
- Open the plugin's **Test** tab, pick the movie and click **Run test**. It shows exactly what would play, or what stopped it (for example "this viewer had a pre-show 10 min ago").

### The pre-roll plays, but the movie doesn't start, or there's a long pause

This usually happens when the app has to convert (transcode) the pre-roll first. Keep **Make compatible copies** on (Playback tab). The plugin then plays an H.264/AAC copy that every app can play. The Files tab warns about files that need it. For your own videos, MP4 files with H.264 video and AAC stereo audio work best.

### My folder shows "Not found on the server"

Type the folder exactly as the **Jellyfin server** sees it:

- **Docker**: the path inside the container, for example `/media/prerolls`, not the path on your computer.
- **Windows**: a full path, for example `D:\Prerolls`.
- The user Jellyfin runs as must be allowed to read the folder.

### What do priority and weight mean?

- **Priority**: when several rules match, only the rules with the highest priority are used. "Christmas" at priority 10 beats "All pre-rolls" at priority 0, but only in December, because that's when the Christmas rule matches.
- **Weight**: among the rules that are used, a pre-roll with weight 3 plays three times as often as one with weight 1.

### How do I set up holiday pre-rolls?

1. Put the videos in a subfolder such as `christmas/`, or tag them "christmas" with **Edit** on the Files tab.
2. On the **Rules** tab choose **Add a holiday rule → Christmas**.
3. Tick "Everything in christmas/" (or the tag), click **Keep rule**, then **Save**.

### Can one movie or show have its own pre-roll?

Yes. Create a folder named `.prerolls` inside the movie's folder, the series folder or a season folder, and put the video in it. It plays only for that title. On the Playback tab you choose whether it replaces the normal pre-roll or plays first.

### How do I stop pre-rolls before a certain movie or show?

Add it under **Titles that never get pre-rolls** (Playback tab), or give it the tag `no-preroll` in Jellyfin's metadata editor.

### How does "age-safe" work?

Give a pre-roll a rating with **Edit** on the Files tab (for example R). With age-safe on, it never plays before a title rated lower, such as PG or a kids' show. Pre-rolls without a rating can play before anything.

### Are my original videos changed?

Never. Converted and volume-matched copies are stored in Jellyfin's cache folder and cleaned up automatically.

### How does the plugin treat prerolls.me?

Gently. The catalog is downloaded at most once an hour and kept on your server; searching never contacts the website; thumbnails are fetched once; downloads happen one at a time, at most 20 per hourly check. Creator credits are kept with every download. Clips hosted on YouTube or Vimeo can only be opened on the website.

### Can I set pre-roll details in files instead?

Yes. Next to `xmas.mp4` create `xmas.mp4.json`:

```json
{ "name": "Christmas Bumper", "tags": ["christmas"], "rating": "G",
  "weight": 2, "from": "12-01", "to": "12-26", "enabled": true }
```

A `prerolls.json` in a folder sets defaults for all files in it:

```json
{ "defaults": { "tags": ["christmas"] }, "files": { "santa.mp4": { "rating": "G" } } }
```

Dates are `MM-dd` for every year, or `yyyy-MM-dd` for one year only. If a file has a mistake, the Files tab names it after a scan.

### I used Local Intros before

**Backup** tab → **Import Local Intros settings**. Then disable Local Intros so you don't get two pre-rolls.

### Still stuck?

[Open an issue](https://github.com/modernharp/jellyfin-plugin-local-prerolls-pro/issues/new/choose) and paste what the **Test** tab says.
