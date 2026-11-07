# native/spikes/windowing: the window primitives native Mantle needs

Small standalone WinUI 3 apps, one per group of primitives, each with an automated check that runs
on a GitHub Windows runner and leaves screenshots and numbers. Nothing outside this folder uses
them. Results are filled in per primitive as the checks pass.

## Layout

- `Directory.Build.props`: the shared build (WinUI 3, unpackaged, self-contained; the same settings
  as native-spike/MantleBar). Each app's csproj only names itself.
- `harness/Common.ps1`: dot-sourced by every check: runner set-up (1920x1080, wallpaper,
  transparency on), `Build-Spike`, screen grabs, memory and CPU of a process tree, `Check` /
  `Note`, and `Finish-Check`, whose last line is the one to judge a run by:
  `WINDOWING <lane>: ok (N of N checks)` or `WINDOWING <lane>: FAILED (...)`.
- `harness/Native.cs`: Win32 helpers for the checks (grab, stray-pixel count, screen size, process tree).
- `smoke/`: the harness smoke run, and the spike's see-through window as is (the baseline).

## Running a check (runner only)

```
pwsh -NoProfile -File native/spikes/windowing/<lane>/check.ps1
```

The checks change the runner's screen size, wallpaper, theme and taskbar, and move the cursor.
Do not run them on a desktop someone is using. Output: `native/spikes/windowing/out/<lane>/`
(results.json, summary.md, screenshots, app logs).
