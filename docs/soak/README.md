# Memory soak evidence

Every memory figure published for Bing Wallpaper Updater (README, release notes) is backed by a CSV in this folder,
produced by `tools/soak.ps1` on the build the CSV names (`# appVersion=`, `# commit=`). No number is typed by hand:
the README row is the output of `tools/soak.ps1 -Summarize <csv>`.

## What is measured, and why these numbers

The app is a .NET 10 WinForms tray process. Three memory numbers describe it, and they differ by design:

| Column | Source | Meaning |
|--------|--------|---------|
| `PrivateWS_MB` | `Win32_PerfFormattedData_PerfProc_Process.WorkingSetPrivate` | **Task Manager's "Memory" column.** Pages only this process uses. This is the NFR-01 number (budget 100 MB). |
| `WS_MB` | `Process.WorkingSet64` | Total working set. Includes ~50 MB of shared, file-backed .NET runtime pages that every .NET process maps; Process Explorer shows it as "Working Set". |
| `Commit_MB` | `Process.PrivateMemorySize64` | Private bytes (commit charge). What the process has asked the OS to back, whether resident or not. |

Alongside them every sample records the classic WinForms leak signals: `Handles` (kernel handles), `Threads`, `GDI` and
`USER` object counts (`GetGuiResources`), plus `Window` (1 while the Settings window is open), `TickIndex` (rotation
ticks completed before the sample), `CyclesDone` (Settings open/close cycles completed) and `CacheCount` (images in
`cache\index.json`).

## The compressed soak

Leak detection is about iteration count, not elapsed time. The app does two things repeatedly in real life - rotation
ticks (every 30 min to 24 h) and Settings window open/close - so the soak drives both a few hundred times in about a
quarter of an hour instead of waiting a day for 48 natural ticks:

1. Launch `publish\BingWallpaperUpdater.exe` from a fresh data folder (`-FreshData`: empty cache, first tick downloads
   the newest UHD image, backfill fills the cache to 10 during the run) and wait for the first `tick done`.
2. `baseline` sample with the window closed.
3. `-SettingsCycles` (20) open/close cycles through the `Local\BingWallpaperUpdater.Show` event and
   `Process.CloseMainWindow`, waiting for the `settings window action=open` / `action=close` log lines; a `settings`
   sample after every fifth cycle, `post-settings` at the end (window closed). Each opened window is minimized at once
   without activation (`ShowWindow SW_SHOWMINNOACTIVE`) so the focus returns to whatever was in front; the burst
   takes about 40 s. `-NoSettingsCycles` skips it.
4. `-Ticks` (200) rotation ticks: the Settings window is opened once, minimized without activation, and its
   **Next wallpaper** button is invoked through UI Automation (`AutomationId` = `NextButton`). Each click is one real
   tick - catalog check on GitHub (304), Bing API, COM wallpaper apply, one backfill download until the cache is full -
   and the tool waits for the `tick done` log line before the next click (`-TickGapSec`, 1 s, between clicks). A
   `ticks` sample is taken every `-SampleIntervalSeconds` (15) right after a tick completes, carrying that tick's index.
   Then the window is closed and a `post-ticks` sample is taken.
5. `final` sample after a 20 s settle with the window closed, verdict, `<csv>.result.txt`.

The default run takes about 7 minutes; the tool prints an ETA line at start. The Settings window still flashes for a
fraction of a second per cycle and the wallpaper changes on every tick, so run it while away from the machine.

The command line behind `win11-soak.csv` (run from the repo root on Windows 11, `publish\` build of the named commit):

```powershell
dotnet publish src/BingWallpaperUpdater.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o publish
powershell -NoProfile -ExecutionPolicy Bypass -File tools/soak.ps1 -FreshData -Csv docs\soak\win11-soak.csv
powershell -NoProfile -ExecutionPolicy Bypass -File tools/soak.ps1 -Summarize docs\soak\win11-soak.csv
```

On a bare Windows 10 1809 VM with the installed build (copy only `tools\soak.ps1`; it attaches to the running app):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File soak.ps1 -NoLaunch -Csv win10-1809-soak.csv
```

## Verdict and thresholds

The verdict is computed from the rows, never from wall-clock time, and is written to `<csv>.result.txt`:

```
SOAK OK privateWS=<max>MB closed=<baseline>-><final>MB ws=<max>MB commit=<max>MB slope=<KB/tick> handles=<ref>-><final> gdi=<ref>-><final> user=<ref>-><final> ticks=<n> cycles=<n> duration=<min>min samples=<n>
SOAK WARN <same fields> reason=<slope|handles|gdi|user> growth
SOAK FAIL: <reason>
```

| Check | Threshold (switch) | Why |
|-------|--------------------|-----|
| **FAIL** - max `PrivateWS_MB` in any sample, window open or closed | >= 100 MB (`-BudgetMB`) | The NFR-01 ceiling, measured the way Task Manager measures it. |
| **WARN** - `slope`: least-squares slope of `PrivateWS_MB` against `TickIndex` over the steady-state `ticks` rows (window open throughout, cache already full so the backfill downloads are excluded) | > 8 KB/tick (`-SlopeWarnKBPerTick`) | 8 KB/tick is ~1.6 MB over 200 ticks, clearly above GC noise (measured within +/-2 MB per sample), and would be ~140 MB over a year at 48 ticks a day - a leak that matters within the product's life. |
| **WARN** - `handles` growth: `final` minus the first sample after the window was opened once | > 100 (`-HandleGrowthWarn`) | A kernel-handle leak from the tick or open/close loop. The reference excludes the one-time WinForms caches (fonts, brushes, the form's own handles) that the first window costs. |
| **WARN** - `gdi` / `user` growth, same reference | > 50 / > 50 (`-GdiGrowthWarn`, `-UserGrowthWarn`) | The classic WinForms leak signal for a form that is opened and closed repeatedly. |

`closed=<baseline>-><final>` shows the private working set with the window closed before and after everything: the
difference is the warm HTTP stack, the WinForms assemblies and the cache index, not growth per iteration.

## CSV layout

Header comments (`# key=value`): `os`, `build`, `appVersion`, `commit`, `startedUtc`, `interval`, `mode`, `resolution`,
`monitorMode`, `cacheAtStart`, `ticks`, `settingsCycles`, `sampleIntervalSeconds`, `tickGapSec`, `cycleGapSec` -
never the computer name, the user name or a profile path. The tool does not touch the registry: the app launched from
`publish\` writes its own HKCU Run value on start (autostart defaults to on); clear it afterwards with
`tools\autostart-probe.ps1 -Cleanup`, as the smoke does. Then the column header
`Utc,ElapsedSec,Phase,TickIndex,CyclesDone,PrivateWS_MB,WS_MB,Commit_MB,Handles,Threads,GDI,USER,Window,CacheCount`
and one row per sample, `Phase` = `baseline` | `settings` | `post-settings` | `ticks` | `post-ticks` | `final`.

## Regenerating the README row

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/soak.ps1 -Summarize docs\soak\win11-soak.csv
```

prints the verdict recomputed from the file and one markdown row:

```
| <os> | <max private WS> MB | <final private WS, window closed> MB | <max WS> MB | <max commit> MB | <slope> KB/tick | compressed soak, <ticks> ticks + <cycles> Settings cycles, ~<min> min | <csv file name> |
```

The README table header that matches the row:

```
| OS | Max private WS (Task Manager "Memory") | Private WS, window closed, end of run | Max working set | Max commit | Private WS slope | Run | Evidence |
```

The label is always "compressed soak, N ticks + M Settings cycles, ~X min" - never "24 h".

## Files

| File | Content |
|------|---------|
| `win11-soak.csv` + `.result.txt` | The Windows 11 measurement of the fixed build (this machine, `publish\` bits of the named commit). |
| `win10-1809-soak.csv` + `.result.txt` | The same run on a Windows 10 LTSC/Enterprise 1809 VM with the installed build - present only when such a VM was available; otherwise the README says "not yet measured". |

## Rules

- The soak tool never changes the app or the runtime to make the number look better: no `SetProcessWorkingSetSize` /
  `EmptyWorkingSet`, no `GCConserveMemory` or trimming knobs. Such a knob may only be proposed after a measurement
  here justifies it.
- The tool touches only the `BingWallpaperUpdater` process (stopped only when the tool launched it) and deletes nothing
  outside `%LocalAppData%\BingWallpaperUpdater`.
- No memory figure is published anywhere that is not backed by a CSV in this folder produced by this tool on the build
  it names.
