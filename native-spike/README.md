# native-spike: the Mantle bar in C# + WinUI 3

A prototype for one decision: should Mantle's UI move from Electron to native C#? It is not a
migration. Nothing outside this folder uses it, and it changes no Mantle file.

## What it draws

Only what the real bar (`ui/bar`) shows at rest:

- the inset pill: min(1120, screen - 28) DIP wide, 48 tall, 14 above the bottom edge, radius 24
  (light) / 18 (dark), with the three island layers (blurred backdrop picture, tint + 1 px hairline,
  1 px lip) and, in light, the drop shadow;
- the Mantle mark on the left;
- the running apps, centred on the island, with their real icons (read from the exe), the running
  line, and the accent line for the app in front. A click brings the app forward, or minimizes it
  when it is already in front;
- on the right: separator, downloads, volume, network, the clock (h:mm:ss AM over the date),
  notifications, more.

Colours, radii, spacing and type come from `ui/shared/glass-light.css` and `ui/bar/bar.css`, as the
`Lg*` resources in `MantleBar/Tokens.xaml` (light and dark).

Not built (each is migration work): flyouts and menus, pins, Store apps (AUMID), tray icons, chips,
media, battery, the real volume level and Wi-Fi bars, look.json (look-sync), the hover reveal and
its motion, drag to reorder, keyboard access.

## Build and run (Windows, .NET SDK only)

```
dotnet publish native-spike/MantleBar/MantleBar.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o native-spike/bin/MantleBar
native-spike/bin/MantleBar/MantleBar.exe --theme=light
```

## Bench

`bench/run-bench.ps1` builds the spike and measures it against the real Electron Mantle on the same
GitHub Windows runner, in one job. It runs Mantle live, so it is for a runner only. See the header
of the script for what it measures.
