English | [Tiếng Việt](README.vi.md)

# Bing Wallpaper Updater

A small Windows tray app that keeps your desktop on the current Bing image, using the [niumoo/bing-wallpaper](https://github.com/niumoo/bing-wallpaper) catalogue - under 100 MB of RAM, no admin rights, no telemetry.

Screenshots: coming soon.

## What it does

- Changes the wallpaper on a schedule you choose (every 30 minutes up to once a day) to the newest Bing image, or to a random one from its small local cache.
- **Next wallpaper** in the tray menu or the Settings window switches immediately.
- Fills all monitors with one image, or gives each monitor its own image.
- Keeps at most 10 images on disk and backfills the previous days after install, so the random mode has something to pick from.
- Speaks English and Vietnamese (follows your Windows display language, or pick one).
- Can start with Windows (per-user, visible in Task Manager > Startup apps, where you can disable it).
- Shows the title and copyright line of the current image.

What it never does:

- never opens a browser or any window on its own (the tray icon is the only thing you see until you open Settings);
- never phones home - there is no telemetry, no crash reporting, no update check;
- never needs administrator rights, for installing, running or uninstalling;
- does not use, install or depend on Microsoft's Bing Wallpaper app - it replaces it.

## Install

1. Download `BingWallpaperUpdater-<version>-x64-Setup.exe` from the [Releases page](https://github.com/anhyeuviolet/BingWallpaperUpdater/releases). Only download it from there.
2. Run it. Windows SmartScreen will say "Windows protected your PC" because the installer is not code-signed (a signing certificate costs money this project does not spend). Click **More info**, then **Run anyway**.
3. **Do not run the installer as administrator** (no right-click > Run as administrator, no elevated terminal). The app installs per user; an elevated installer would put it into the administrator's profile instead of yours, and it cannot launch the app at the end. The installer warns you and preselects Cancel if that happens.
4. Pick the installer language (English or Tiếng Việt). There is no folder page: the app always goes to `%LocalAppData%\Programs\BingWallpaperUpdater\`.
5. Leave **Start with Windows** ticked if you want the wallpaper to keep rotating after a reboot. You can change this later in Settings.
6. Finish. The app starts, downloads the newest Bing image and sets it; the tray icon appears next to the clock.

The installer is about 34 MB because it bundles the .NET 10 runtime (self-contained). Nothing else has to be installed and there is no UAC prompt.

### Verify the download

Every release carries a `.sha256` file next to the installer. Compare it with the hash of what you downloaded:

```powershell
Get-FileHash .\BingWallpaperUpdater-<version>-x64-Setup.exe -Algorithm SHA256
```

or, on a machine without `Get-FileHash`:

```cmd
certutil -hashfile BingWallpaperUpdater-<version>-x64-Setup.exe SHA256
```

The hex string must equal the first field of `BingWallpaperUpdater-<version>-x64-Setup.exe.sha256`. The installer is built only by GitHub Actions from the version tag; the workflow run is linked from each release.

Every release also carries a GitHub build-provenance attestation, which proves the file came from this repository's workflow:

```powershell
gh attestation verify BingWallpaperUpdater-<version>-x64-Setup.exe -R anhyeuviolet/BingWallpaperUpdater
```

## What it touches

Everything is per user. Nothing under `HKLM`, `Program Files` or Task Scheduler.

| Where | What |
|-------|------|
| `%LocalAppData%\Programs\BingWallpaperUpdater\` | The app and the bundled .NET runtime (about 220 files), plus `unins000.exe` / `unins000.dat` (the uninstaller). |
| `%LocalAppData%\BingWallpaperUpdater\` | Data root: `settings.json` (your settings), `state.json` (schedule and current image), `log.txt` (plain-text log, rolls at 256 KB), `cache\index.json` (image metadata), `cache\catalog.md` (the last catalogue page), `cache\*.jpg` (at most 10 images). |
| `%AppData%\Microsoft\Windows\Start Menu\Programs\Bing Wallpaper Updater.lnk` | Start-menu shortcut. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\{4AD7C4B2-6A1C-443D-95F9-74E5239D058C}_is1` | The uninstall entry shown in Settings > Apps. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\BingWallpaperUpdater` | `"<install dir>\BingWallpaperUpdater.exe" --startup` - present while Start with Windows is on. |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\BingWallpaperUpdater` | Windows' own enabled/disabled flag for that Run entry. Written only after you toggle Start with Windows in Settings (the installer never writes it). |
| `HKCU\Control Panel\Desktop` - `WallpaperStyle` = `10`, `TileWallpaper` = `0` | Written only when the `IDesktopWallpaper` COM interface is unavailable and the app falls back to `SystemParametersInfo`. Windows itself also records the wallpaper path there (`Wallpaper`, `TranscodedWallpaper`) whenever any app sets a wallpaper. |

## Settings

Open the Settings window from the tray icon (right-click > **Settings**), or simply launch the app a second time from the Start menu: a second launch does not start another instance, it opens the Settings window of the running one.

| Option | Values |
|--------|--------|
| Check every | 30 minutes, 1 hour, 2 hours, 4 hours, 8 hours, 24 hours |
| Mode | Newest (always the current Bing image) or Random (any image in the cache) |
| Resolution | Auto (largest monitor), UHD (3840x2160), 1920x1200, 1920x1080 |
| Bing market | en-US (default; matches the catalogue), en-GB, en-AU, en-CA, en-IN, de-DE, fr-FR, ja-JP, zh-CN, vi-VN |
| Monitors | Same image on all monitors, or one image per monitor |
| Start with Windows | on / off (writes or removes the HKCU Run value above) |
| Language | Windows display language, English, Tiếng Việt |

The **Current image** box shows the title, copyright, date, last check, next check and the last error, if any. **Next wallpaper** switches now; **Open cache folder** opens `%LocalAppData%\BingWallpaperUpdater\cache\` in Explorer. Changes are saved as soon as you make them.

## Uninstall

Settings > Apps > Installed apps > **Bing Wallpaper Updater** > Uninstall (or the uninstaller in the install folder).

- The running app is asked to exit and closes itself; you do not have to quit it first.
- The uninstaller asks: *Also delete the downloaded wallpapers and settings in %LocalAppData%\BingWallpaperUpdater?* **No** is preselected. Answer Yes to remove the data root as well.
- Either way it removes the install folder, the Start-menu shortcut, the uninstall entry, the Run value and the StartupApproved value.
- Your current desktop wallpaper stays until you change it.

## Memory

Measured with [`tools/soak.ps1`](tools/soak.ps1) on the published build, starting from a fresh data folder: 20 Settings window open/close cycles, then 200 wallpaper changes driven through the Next button, with a sample every 15 seconds. The whole run took 6.4 minutes (it is a compressed soak: leaks show up per iteration, not per hour). The CSV and the verdict line are committed under [`docs/soak/`](docs/soak/) and the row below is printed by `tools/soak.ps1 -Summarize docs/soak/win11-soak.csv` - no number here is typed by hand.

| OS | Max private WS (Task Manager "Memory") | Private WS, window closed, end of run | Max working set | Max commit | Private WS slope | Run | Evidence |
|----|----------------------------------------|---------------------------------------|-----------------|------------|------------------|-----|----------|
| Microsoft Windows 11 IoT Enterprise LTSC 10.0.26100 | 30.6 MB | 26.1 MB | 106.5 MB | 36.4 MB | -6.72 KB/tick | compressed soak, 200 ticks + 20 Settings cycles, ~7 min | win11-soak.csv |
| Windows 10 LTSC/Enterprise 1809 | not yet measured | | | | | | |

"Private WS" is what Task Manager's Memory column shows and what the under-100 MB promise is about: pages only this process uses. The total working set is larger because it includes about 50 MB of shared, file-backed .NET runtime pages that every .NET process maps and Windows counts once. The peak (30.6 MB) is with the Settings window open; with it closed the app sits at 19-26 MB.

Note on the committed run: it ends with `SOAK WARN reason=gdi growth` because, on that build, each Settings window open/close leaked about 7 GDI brushes in dark mode (14 -> 160 over 20 cycles; a WinForms .NET 10 dark-mode brush-ownership bug). This is fixed in commit `376652c` (the window and its drop-downs now own their background brushes); a re-run of `tools/soak.ps1 -Ticks 0 -SettingsCycles 20` on the fixed build reports `SOAK OK ... gdi=33->33`. Private working set, handles and USER objects never grew.

Evidence: [`docs/soak/win11-soak.csv`](docs/soak/win11-soak.csv) and [`docs/soak/win11-soak.csv.result.txt`](docs/soak/win11-soak.csv.result.txt). The Windows 10 1809 row will be filled in the same way once such a machine is available.

## Supported Windows versions

| Windows | Support |
|---------|---------|
| Windows 11 (x64) | Supported, measured |
| Windows 10 LTSC / Enterprise 1809 and later (x64) | Supported (the .NET 10 runtime's support list); not yet measured |
| Other Windows 10 builds (x64) | Best effort - consumer Windows 10 is past end of life, the runtime is no longer tested on it |

The installer refuses to run below Windows 10 build 17763 (version 1809). Only x64 is built; Arm64 Windows runs it under emulation and is untested.

## Network

The app talks to exactly three hosts, all over HTTPS, and nothing else:

| Host | Purpose |
|------|---------|
| `raw.githubusercontent.com` | The niumoo/bing-wallpaper catalogue (README and monthly pages), fetched with a conditional GET (`If-None-Match`, so an unchanged page is a 304 with no body) |
| `www.bing.com` | The Bing image archive API (title and copyright) and the image files |
| `cn.bing.com` | Mirror for the image files, used when `www.bing.com` fails |

Per scheduled check it downloads at most one new image plus, while the cache is not yet full, one older image (backfill). There is no telemetry, no update check, no other host - the host allow-list is enforced in the code and a request to anything else is refused before it is sent. The `User-Agent` is `BingWallpaperUpdater/<version> (+https://github.com/anhyeuviolet/BingWallpaperUpdater)`.

## FAQ

**The wallpaper keeps switching back to something else.** Windows Spotlight or Windows settings sync is re-applying its own wallpaper. Turn Spotlight off (Settings > Personalization > Background > Personalize your background: Picture) or exclude the wallpaper from sync (Settings > Accounts > Windows backup > Remember my preferences > Personalization).

**The wallpaper looks more compressed than the downloaded file.** Windows re-encodes every wallpaper to JPEG at its own quality when it applies it. The registry value `JPEGImportQuality` under `HKCU\Control Panel\Desktop` controls that quality; this app does not set it, and this is only a mention - change it at your own risk.

**I do not see the tray icon on Windows 11.** Windows 11 hides new tray icons in the overflow area (the `^` next to the clock). Open the overflow and drag the icon onto the taskbar, or turn it on under Settings > Personalization > Taskbar > Other system tray icons.

**Can I run the installer as administrator to install for everyone?** No. The app is per-user by design, and an elevated installer would install into the administrator's profile and could not launch the app. Do not run the installer as administrator; each user installs it once.

**Why does the cache fill up over the first days, and which image goes first once it is full?** After install the app has only today's image. On each scheduled check it downloads one older day from the catalogue (backfill) until the cache holds 10 images, so Random mode gets a real choice within a few checks. Once the cache holds 10 images, the oldest download is evicted first (smallest `DownloadedUtc` that is not currently applied), so the older days backfilled after install are evicted last - accepted v1 behaviour. For markets other than en-US the catalog is Bing's 8-day HPImageArchive page, so the backfill depth is 8 there instead of 9.

**Why is the installer about 34 MB?** It contains the whole .NET 10 runtime, so nothing has to be installed system-wide and there is never a UAC prompt. A framework-dependent build would be a few MB but would need the .NET Desktop Runtime installed per machine, with elevation.

**Why does SmartScreen warn?** The installer is not signed with a code-signing certificate. Verify the SHA-256 as described above, then More info > Run anyway.

## Build from source

- .NET 10 SDK (the exact version is pinned in `global.json`; `dotnet --version` should print 10.0.4xx).
- Tests: `dotnet test tests/BingWallpaperUpdater.Core.Tests -c Release`
- Installer: `powershell -File tools/build-installer.ps1 -Version 0.0.0` (needs Inno Setup 6.7.x; the script publishes the app self-contained and compiles `installer/setup.iss` into `dist/`).
- Checks used during development, all PowerShell 5.1: `tools/install-probe.ps1` (silent install / uninstall round trip), `tools/smoke-verify.ps1` (log-based smoke of a running instance), `tools/soak.ps1` (the memory soak above), `tools/settings-size-probe.ps1`, `tools/autostart-probe.ps1`.

## Release process

1. Push a tag `vX.Y.Z` on `master`. It must match `<Version>` in `Directory.Build.props`: the workflow compares the two and fails the build on a mismatch, so bump the props first.
2. GitHub Actions (`.github/workflows/release.yml`, on a pinned `windows-2025` runner) runs the tests, publishes the app, compiles the installer with Inno Setup 6.7.1, writes `BingWallpaperUpdater-X.Y.Z-x64-Setup.exe.sha256`, verifies it with `sha256sum -c`, and opens a **draft** release named `BingWallpaperUpdater X.Y.Z` with those two files attached.
3. The maintainer writes the release notes by hand from `.github/RELEASE_NOTES_TEMPLATE.md` (highlights, the SmartScreen sentence, the SHA-256, the memory row, supported Windows) and clicks Publish. Nothing is published automatically.

## Credits

- [niumoo/bing-wallpaper](https://github.com/niumoo/bing-wallpaper) - the daily catalogue of Bing image IDs that makes the backfill and the offline history possible.
- The images are Bing's; copyright belongs to Microsoft and the respective photographers. The app shows the copyright line of every image it sets.
- [Inno Setup](https://jrsoftware.org/isinfo.php) - the installer (free for non-commercial use).
- [.NET](https://dotnet.microsoft.com/) and Windows Forms.

## Licence

MIT - see [LICENSE](LICENSE). Copyright (c) 2026 Kenny Nguyen (anhyeuviolet).
