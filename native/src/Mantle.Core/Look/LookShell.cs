using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Logging;

namespace Mantle.Core.Look;

/// <summary>The <c>theme</c> event (main.js themePayload): the theme view plus the <c>--lg-*</c> overrides for that theme.</summary>
public sealed record ThemePayload(ThemeView View, JsonObject Look)
{
    public string Theme => View.Theme;
}

/// <summary>How the shell's look is put together (App.xaml.cs fills it from the run; tests fill it with fakes).</summary>
public sealed record LookShellOptions
{
    /// The ONE gate's stores: theme.json and look-sync.json.
    public required IStores Stores { get; init; }
    /// The ONE gate's writer, for look.json (a --live sole shell only writes the user's own; test and scratch runs their own folder).
    public required SafeFile Files { get; init; }
    /// DataPaths.LookSyncDir of the run.
    public required string LookDir { get; init; }
    /// Windows' app mode (ThemeCore.ReadWindowsMode of the watched key).
    public required Func<WindowsMode> WindowsMode { get; init; }
    /// Runs an action on the host thread.
    public required Action<Action> Post { get; init; }
    public required IEvents Events { get; init; }
    public required ILogSink Log { get; init; }
    /// --theme= of a test run: pinned, never saved, and a shared theme never overrides it.
    public string? PinnedTheme { get; init; }
    /// Tests shorten look sync's delays here.
    public Func<LookSyncOptions, LookSyncOptions>? Tune { get; init; }
    public int GuardGraceMs { get; init; }
}

/// <summary>
/// main.js getTheme() + getLook() + their COMMANDS, in one place: the theme core (theme.json), the look host
/// (look-sync.json + look.json shared with Helm and Quartz), the <c>theme.*</c> and <c>look.*</c> commands,
/// and the rebroadcast: every change of the theme, or of its overrides, is published as the <c>theme</c>
/// event (and <see cref="Shown"/>), every look change as <c>look</c>, on the host thread.
/// Threading: look sync applies another app's change on its own thread, inside the look host's lock, and that
/// sets the theme; the host thread sets it from commands and from Windows. So the theme core (single-threaded
/// by design) is reached only under one lock here, and its listener only POSTS (lock order is always look host,
/// then theme, never the other way round).
/// </summary>
public sealed class LookShell : IDisposable
{
    public const string Owner = "look";
    readonly LookShellOptions _o;
    readonly object _theme = new();
    readonly string? _pinned;
    IDisposable? _themeOff, _lookOff, _follow;
    bool _closed;

    public ThemeCore Theme { get; }
    public LookHost Look { get; }

    /// After every change of the theme or of its overrides, on the host thread (the surfaces' feed).
    public event Action<ThemePayload>? Shown;

    public LookShell(LookShellOptions o)
    {
        _o = o ?? throw new ArgumentNullException(nameof(o));
        _pinned = ThemeCore.IsMode(o.PinnedTheme) ? o.PinnedTheme : null;
        // theme.json is saved BEHIND the tell (core-6.md): every surface hears the pick at once, the save (still flushed to
        // disk) lands after it, in order, and its outcome comes back on the host thread under the theme lock
        Theme = new ThemeCore(o.Stores.Open("theme.json") as JsonStore, o.WindowsMode, _pinned, o.Log, work => o.Post(() => { lock (_theme) work(); }));
        var lt = new LookTheme(
            () => { lock (_theme) return Theme.Mode; },
            () => { lock (_theme) return Theme.Theme; },
            m => {
                if (_pinned != null) return "this test run is pinned by --theme=" + _pinned;
                lock (_theme) return Theme.Set(JsonValue.Create(m)) is { Ok: false } r ? r.Reason ?? "it was refused" : null;
            });
        var sync = new LookSyncOptions { App = "mantle", Dir = o.LookDir, Files = o.Files, Log = o.Log.Line };
        if (o.Tune != null) sync = o.Tune(sync);
        Look = new LookHost(o.Stores.Open("look-sync.json"), sync, lt, o.Log, o.GuardGraceMs);
        _themeOff = Theme.On(v => o.Post(() => Told(v)));
        _lookOff = Look.OnChange(e => o.Post(() => Moved(e)));
        if (_pinned != null) o.Log.Line($"theme: --theme={_pinned}: this test run shows {Theme.Theme} and never saves a change");
    }

    /// The theme every surface shows now ("dark" | "light").
    public string Now { get { lock (_theme) return Theme.Theme; } }

    /// The <c>theme</c> event's payload now.
    public ThemePayload Payload()
    {
        ThemeView v; lock (_theme) v = Theme.Get();
        return Payload(v);
    }

    ThemePayload Payload(ThemeView v)
    {
        JsonObject vars;
        try { vars = Look.VarsJson(v.Theme); }
        catch (Exception e) { _o.Log.Line("look: the look overrides could not be worked out: " + e.Message); vars = new JsonObject(); }
        return new ThemePayload(v, vars);
    }

    /// <summary>Start look sync (read the shared look, seed when missing, watch it). main.js: getLook().start().</summary>
    public LookShell Start()
    {
        try { Look.Start(); } catch (Exception e) { _o.Log.Line("look: look sync could not start: " + e.Message); }
        return this;
    }

    /// <summary>Follow Windows' app mode live (ThemeFollow on <paramref name="subKey"/>, a scratch key in tests).</summary>
    public void FollowWindows(string subKey)
    {
        if (!OperatingSystem.IsWindows()) { _o.Log.Line("theme: not Windows, so Match Windows is read only at start"); return; }
        _follow = ThemeFollow.Start(() => WindowsChanged(), _o.Post, _o.Log, subKey);
    }

    /// Windows' app mode may have changed (host thread): re-read it; listeners hear only a real move.
    public bool WindowsChanged()
    {
        try { lock (_theme) return Theme.WindowsChanged(); }
        catch (Exception e) { _o.Log.Line("theme: Windows' light/dark change could not be followed: " + e.Message); return false; }
    }

    /// theme.json changed on disk (host thread): the host's theme follows it, so every surface hears it through "theme".
    public bool ThemeFileChanged()
    {
        if (_closed) return false;
        try { lock (_theme) return Theme.FileChanged(); }
        catch (Exception e) { _o.Log.Line("theme: theme.json's change could not be followed: " + e.Message); return false; }
    }

    /// Re-read the shared look: on resume ("the PC woke from sleep") and on unlock ("the screen was unlocked").
    public void Refresh(string why) { if (!_closed) Look.Refresh(why); }

    void Told(ThemeView v)
    {
        if (_closed) return;
        var p = Payload(v);
        Tell(p);
        // look sync: share a theme the user picked here (look-host never re-shares one it applied from sync)
        string mode; lock (_theme) mode = Theme.Mode;
        _ = Look.ThemeChangedAsync(mode).ContinueWith(t => _o.Log.Line("look: sharing the theme failed: " + t.Exception?.GetBaseException().Message),
            System.Threading.CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    void Moved(LookChange e)
    {
        if (_closed) return;
        if (e.VarsChanged) Tell(Payload());
        _o.Events.Publish("look", e.View);
    }

    void Tell(ThemePayload p)
    {
        _o.Events.Publish("theme", p);
        try { Shown?.Invoke(p); } catch (Exception x) { _o.Log.Line("theme: a surface failed to take the theme: " + x.Message); }
    }

    // ---- the commands (main.js COMMANDS theme.* / look.*), registered by the generated catalog ----------

    [Command("theme.get")]
    public CommandResult ThemeGet(CommandCall call) { ThemeView v; lock (_theme) v = Theme.Get(); return Answer(ViewJson(v)); }

    [Command("theme.set")]
    public async ValueTask<CommandResult> ThemeSet(CommandCall call)
    {
        JsonNode? mode = call.Args.ValueKind == JsonValueKind.Object && call.Args.TryGetProperty("mode", out var m) ? JsJson.FromElement(m) : null;
        ThemeSetResult r; lock (_theme) r = Theme.Set(mode);
        if (!r.Ok || r.View == null) return Answer(new JsonObject { ["ok"] = false, ["reason"] = r.Reason });
        var view = r.View;
        // every surface has been told already; the answer waits for the save so it carries a failed save's note (LOOK-010)
        if (r.Saved is { IsCompleted: false } saved) { await saved.ConfigureAwait(true); lock (_theme) view = Theme.Get(); }
        var o = ViewJson(view); o["value"] = r.Value;
        return Answer(o);
    }

    /// Done when theme.json's writes asked so far have landed (the quit waits for it, bounded).
    public Task SavedAsync() => Theme.Writes;

    [Command("look.get")]
    public CommandResult LookGet(CommandCall call) => Answer(Look.View());

    [Command("look.sync")]
    public async ValueTask<CommandResult> LookSyncSet(CommandCall call)
        => Answer(await Look.SetSyncAsync(Arg(call, "on")).ConfigureAwait(true));

    [Command("look.glass")]
    public async ValueTask<CommandResult> LookGlass(CommandCall call)
        => Answer(await Look.SetGlassAsync(call.Args.ValueKind == JsonValueKind.Object ? JsJson.FromElement(call.Args) as JsonObject : null).ConfigureAwait(true));

    [Command("look.accent")]
    public async ValueTask<CommandResult> LookAccent(CommandCall call)
    {
        if (call.Args.ValueKind != JsonValueKind.Object || !call.Args.TryGetProperty("hue", out _))
            return Answer(new JsonObject { ["ok"] = false, ["reason"] = "the accent change came without a hue (null for Mantle's own)" });
        return Answer(await Look.SetAccentAsync(Arg(call, "hue")).ConfigureAwait(true));
    }

    static JsonNode? Arg(CommandCall call, string key)
        => call.Args.ValueKind == JsonValueKind.Object && call.Args.TryGetProperty(key, out var v) ? JsJson.FromElement(v) : null;

    /// lib/theme.js view(), key for key.
    public static JsonObject ViewJson(ThemeView v) => new() {
        ["ok"] = v.Ok, ["mode"] = v.Mode, ["theme"] = v.Theme, ["windows"] = v.Windows, ["windowsWhy"] = v.WindowsWhy,
        ["pinned"] = v.Pinned, ["note"] = v.Note, ["reason"] = v.Reason,
    };

    static CommandResult Answer(JsonObject o)
    {
        using var d = JsonDocument.Parse(o.ToJsonString());
        return CommandResult.Success(d.RootElement.Clone());
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _follow?.Dispose(); _themeOff?.Dispose(); _lookOff?.Dispose();
        Look.Dispose();
    }
}
