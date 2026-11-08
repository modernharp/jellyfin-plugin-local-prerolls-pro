# Publishing checklist (for the repository owner)

How to put this on GitHub and make the first release. You only do steps 1–4 once.

## 1. Create the repository

1. On GitHub, click **+ → New repository**.
2. Owner `modernharp`, name **`jellyfin-plugin-local-prerolls-pro`**, **Public**.
3. Don't add a README, .gitignore or license (they're already here). Click **Create repository**.

## 2. Upload the files

The `.github` folder is hidden on Mac and Linux. It holds the automatic release builds, so it **must** be uploaded too. The easiest reliable way is git:

```bash
cd jellyfin-plugin-local-prerolls-pro
git init -b main
git add .
git commit -m "Local Pre-Rolls Pro 1.0.0"
git remote add origin https://github.com/modernharp/jellyfin-plugin-local-prerolls-pro.git
git push -u origin main
```

Or use **GitHub Desktop**: *File → Add local repository* → choose this folder → *Publish repository*.

Check on GitHub that you can see the `.github` folder and the green **Build** check on the latest commit (Actions tab).

## 3. Make it look good

- **About** (gear icon next to "About" on the repo page):
  - Description: `Cinema-style pre-rolls for Jellyfin: rules, holiday intros, free prerolls.me catalog, auto-convert and volume matching`
  - Website: `https://www.prerolls.me`
  - Topics: `jellyfin`, `jellyfin-plugin`, `prerolls`, `pre-roll`, `cinema-mode`, `home-theater`, `media-server`
- **Social preview** (shown when the link is shared): **Settings → General → Social preview → Edit → Upload an image** → choose `images/banner.png` (1280×640).
- Optional: **Settings → General → Features**: turn on **Discussions** for questions.

## 4. Allow the release build to update the plugin list

**Settings → Actions → General → Workflow permissions** → choose **Read and write permissions** → **Save**. This lets the release workflow add each new version to `manifest.json`, which Jellyfin reads. If you protect the `main` branch, allow GitHub Actions to push to it.

## 5. Publish a release (every time)

1. Add a section for the new version at the top of `CHANGELOG.md` (for example `## [1.0.0] - 2026-11-08`) and push it. Version 1.0.0 is already written.
2. On GitHub: **Releases → Draft a new release**.
3. **Choose a tag** → type `v1.0.0` → **Create new tag on publish**.
4. Title: `Local Pre-Rolls Pro 1.0.0`. **Leave the description empty**: the build writes friendly download and install notes for you.
5. Click **Publish release**.

The **Release** workflow (Actions tab, about 2 minutes) then:

- runs the tests and builds the plugin;
- attaches `local-prerolls-pro_1.0.0.0.zip` and `local-prerolls-pro.zip` to the release;
- writes the release notes (download link, install steps, what's new);
- adds the version to `manifest.json`, so Jellyfin servers that added the repository see the update.

After it finishes, check:

- The release page has the two zip files and the notes.
- https://raw.githubusercontent.com/modernharp/jellyfin-plugin-local-prerolls-pro/main/manifest.json lists the version.
- The **Download** button in the README works: https://github.com/modernharp/jellyfin-plugin-local-prerolls-pro/releases/latest/download/local-prerolls-pro.zip

If something failed, open **Actions → Release** to see why. After fixing it, use **Run workflow** and enter the tag (e.g. `v1.0.0`) to rebuild that release.

## Versions

Use tags like `v1.0.0`, `v1.1.0`, `v1.1.1`. Jellyfin shows them as `1.0.0.0` and so on. The plugin targets Jellyfin 12 (`targetAbi` in `scripts/plugin.json`).

## Build a zip on your own computer

Needs the .NET 10 SDK:

```bash
scripts/package.sh 1.0.0      # -> dist/local-prerolls-pro_1.0.0.0.zip
```
