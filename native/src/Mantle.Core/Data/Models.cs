using System;
using System.Collections.Generic;
using System.Globalization;

namespace Mantle.Core.Data;

/// theme.json "mode" (lib/theme.js): what the user picked. System = Match Windows (the default).
public enum ThemeMode { System, Light, Dark }

/// <data root>\theme.json
public sealed record ThemeSettings(ThemeMode Mode)
{
    public static readonly ThemeSettings Default = new(ThemeMode.System);
    public static string Word(ThemeMode m) => m switch { ThemeMode.Light => "light", ThemeMode.Dark => "dark", _ => "system" };
}

/// One theme's glass (lib/look-sync.js RANGES: blurPx 0..64, dim 0.20..0.90, panel -0.30..0.15).
public sealed record GlassLook(double BlurPx, double Dim, double Panel)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"blur {BlurPx} dim {Dim} panel {Panel}");
}

/// The glass for both themes plus how it renders ("picture" or "seeThrough").
public sealed record GlassSettings(GlassLook Dark, GlassLook Light, string Render)
{
    /// lib/look-sync.js SEED.glass: dark blur 5 / dim .50, light blur 12 / dim .45, panel 0, picture.
    public static readonly GlassSettings Seed = new(new GlassLook(5, 0.50, 0), new GlassLook(12, 0.45, 0), "picture");
    public GlassLook For(bool dark) => dark ? Dark : Light;
    public override string ToString() => $"light {Light}, dark {Dark}, render {Render}";
}

/// <data root>\look-sync.json, Mantle's own copy of the look (lib/look-host.js): what it shows while
/// sync is off and before the shared file is read. AccentHue null = Mantle's own accent.
public sealed record LocalLook(bool LookSync, int? AccentHue, GlassSettings Glass)
{
    /// lib/look-host.js L: lookSync true, accent { hue: null }, glass = the seed.
    public static readonly LocalLook Default = new(true, null, GlassSettings.Seed);
    public override string ToString() => $"look sync {(LookSync ? "on" : "off")}, accent {(AccentHue?.ToString(CultureInfo.InvariantCulture) ?? "Mantle's own")}, glass {Glass}";
}

/// The desktop wallpaper record Mantle publishes in look.json (kept as read; the bar does not use it yet).
public sealed record DesktopRecord(long Rev, bool Ok, string? Id, string? Type, string? Title, string? File, string? Still, string? Reason);

/// %APPDATA%\look-sync\look.json, the look shared by Helm, Quartz and Mantle (lib/look-sync.js, schema 1).
/// A field is null when the file does not carry a usable one.
public sealed record SharedLook(int Schema, long Rev, string? WriterApp, string? WriterAt,
    ThemeMode? Theme, bool HasAccent, int? AccentHue, GlassSettings? Glass, DesktopRecord? Desktop)
{
    public static readonly SharedLook None = new(0, 0, null, null, null, false, null, null, null);
    public override string ToString()
        => $"rev {Rev.ToString(CultureInfo.InvariantCulture)} by {WriterApp ?? "?"}, theme {(Theme is { } t ? ThemeSettings.Word(t) : "-")}, accent {(HasAccent ? AccentHue?.ToString(CultureInfo.InvariantCulture) ?? "own" : "-")}, glass {(Glass?.ToString() ?? "-")}{(Desktop != null ? ", desktop record" : "")}";
}

/// <data root>\features.json: the on/off switch of each feature by name (lib/features.js).
/// A feature that is not listed is at its own default (which only Electron knows).
public sealed record FeatureSwitches(IReadOnlyDictionary<string, bool> Switches)
{
    public static readonly FeatureSwitches Default = new(new Dictionary<string, bool>(StringComparer.Ordinal));
    /// The saved switch, or null when the user never flipped it.
    public bool? Get(string feature) => Switches.TryGetValue(feature, out bool on) ? on : null;
    public override string ToString() => Switches.Count + " switch(es)";
}

/// One entry of Mantle's own pin overlay.
public sealed record PinEntry(string Id, string? Name, string? Exe, string? Aumid, string? Shortcut);

/// <data root>\features\jumplist.json "pins" (lib/features/_jumplist-pins.js): Mantle's own pins
/// (Add), Windows pins it hides (Hide), and the order the user dragged them to (Order, pin ids).
public sealed record PinOverlay(IReadOnlyList<PinEntry> Hide, IReadOnlyList<PinEntry> Add, IReadOnlyList<string> Order)
{
    public static readonly PinOverlay Empty = new(Array.Empty<PinEntry>(), Array.Empty<PinEntry>(), Array.Empty<string>());
    public override string ToString() => $"add {Add.Count}, hide {Hide.Count}, order {Order.Count}";
}
