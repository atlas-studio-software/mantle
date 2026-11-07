using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Keys;
using Mantle.Core.State;

namespace Mantle.Core.Features.Tray;

/// <summary>What the tray needs from the rest of Mantle (main.js updateTrayNow's o, TRAY_ACTIONS, copyDiagnosticReport).
/// A missing action is not silent: choosing it shows a notice that says it is not part of this build.</summary>
public sealed class TrayOptions
{
    public required ITrayIcon Icon { get; init; }
    public required HostClock Clock { get; init; }
    public required ICommandBus Commands { get; init; }
    public bool Test { get; init; }
    /// The shortcuts and the tray's own failure go in its piece.
    public IShellStateHub? State { get; init; }
    public FeatureHost? Features { get; init; }
    public Action<string>? Log { get; init; }
    /// The state's problems() strip (native-core).
    public Func<IReadOnlyList<TrayProblem>>? Problems { get; init; }
    public Func<string>? SidecarStatus { get; init; }
    public Func<bool>? SidecarGaveUp { get; init; }
    /// The crash-recovery line, shown for an hour (null = none).
    public Func<string?>? Recovery { get; init; }
    /// The updates feature's tray label (null = no update out).
    public Func<string?>? Update { get; init; }
    /// Show the launcher; null when it opened, else why not.
    public Func<string, string?>? ShowLauncher { get; init; }
    /// Open the wallpaper library window; null when it opened, else why not.
    public Func<string, string?>? OpenWallpapers { get; init; }
    /// Stand the crash supervisor down (first, so a crash during the restore cannot bring Mantle back).
    public Action<string>? StandDown { get; init; }
    /// The verified restore (Mantle stays open).
    public Func<string, Task>? Restore { get; init; }
    /// shutdown(why).
    public Action<string>? Quit { get; init; }
    /// The diagnostic report's text (native-core builds the snapshot with TrayReport.Build).
    public Func<string, ValueTask<string>>? Report { get; init; }
    /// Live: the clipboard (default Win32TrayIcon.CopyText). Null when copied, else why not.
    public Func<string, string?>? CopyText { get; init; }
    /// A test run: write the report into the test profile through the store's gate (never the clipboard). Null when written, else why not.
    public Func<string, string?>? WriteTestReport { get; init; }
    public TimeSpan Window { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// The tray, host side (main.js createTray, updateTrayNow, trayNotice, TRAY_ACTIONS, copyDiagnosticReport): the
/// menu is rebuilt through a <see cref="TrayBatch"/> (at most once per 250 ms, only when its inputs moved), the
/// tooltip separately, and a chosen row runs its action. A failed action says so in a balloon (warning, no sound)
/// as well as the log. Host thread only.
/// </summary>
public sealed class TrayController : IDisposable
{
    readonly TrayOptions _o;
    readonly TrayBatch _batch;
    readonly Action<string> _log;
    string? _tip;
    string? _sig;
    bool _up;

    public TrayController(TrayOptions o)
    {
        _o = o ?? throw new ArgumentNullException(nameof(o));
        _log = o.Log ?? (_ => { });
        _batch = new TrayBatch(o.Clock, Key, UpdateNow,
            (e, where) => _log($"the tray menu {(where == "key" ? "inputs could not be read" : "could not be rebuilt")}: {e.Message}"), o.Window);
    }

    /// Paused: Windows was put back from the tray and Mantle stays open (main.js shellPaused).
    public bool Paused { get; private set; }
    public TrayInputs? LastInputs { get; private set; }
    public IReadOnlyList<TrayItem> LastItems { get; private set; } = Array.Empty<TrayItem>();
    public TrayBatch Batch => _batch;
    public ITrayIcon Icon => _o.Icon;

    /// <summary>Put the icon up and build the menu. Null when it is up, else why not (also logged, and the
    /// shortcuts' reason says the tray is missing: the kill switch and the desktop strip are the other ways out).</summary>
    public string? Start()
    {
        _o.Icon.Chosen = Choose;
        string? err;
        try
        {
            var i = Inputs();
            err = _o.Icon.Create(TrayModel.Health(i.Problems, i.Test, i.Paused, i.SidecarStatus, i.SidecarGaveUp).Tip);
        }
        catch (Exception e) { err = e.Message; }
        if (err != null)
        {
            _log($"could not create the tray icon: {err}");
            _o.State?.Set(s => s with { Shortcuts = ShellShortcuts.NoTray(s.Shortcuts, err) });
            return err;
        }
        _up = true;
        _log(_o.Icon.Draws ? "tray: icon up" : "tray: test run: a stand-in tray keeps the menu and draws no icon");
        Force();
        return null;
    }

    /// Something the menu shows may have moved: rebuild within the window, if it did.
    public void Request() { if (_up) _batch.Request(); }
    /// Rebuild now.
    public void Force() { if (_up) _batch.Force(); }

    TrayInputs Inputs()
    {
        var sc = _o.State?.Current.Shortcuts;
        var rows = _o.Features?.TrayRows() ?? Array.Empty<FeatureTrayRow>();
        return new TrayInputs(
            _o.Test, Paused, _o.Problems?.Invoke() ?? Array.Empty<TrayProblem>(), _o.Recovery?.Invoke(),
            _o.SidecarStatus?.Invoke() ?? "ok", _o.SidecarGaveUp?.Invoke() ?? false,
            sc?.Launcher ?? false, sc?.KillSwitch ?? false, _o.Commands.Has("settings.open"),
            TrayModel.FeatureItems(rows), _o.Update?.Invoke());
    }

    object?[] Key()
    {
        var i = Inputs();
        return new object?[]
        {
            i.Test, i.Paused, string.Join("\n", i.Problems.Select(p => p.Piece + "\u0001" + p.Reason)), i.Recovery, i.SidecarStatus, i.SidecarGaveUp,
            i.LauncherKey, i.KillSwitchKey, i.HasSettings, TrayModel.Signature(i.FeatureItems), i.Update,
        };
    }

    void UpdateNow()
    {
        var o = Inputs();
        var items = TrayModel.Template(o);
        var tip = TrayModel.Health(o.Problems, o.Test, o.Paused, o.SidecarStatus, o.SidecarGaveUp).Tip;
        LastInputs = o;
        if (tip != _tip) { _o.Icon.SetTip(tip); _tip = tip; }
        var sig = TrayModel.Signature(items);
        LastItems = items;
        if (sig == _sig) return;
        _o.Icon.SetMenu(items);
        _sig = sig;
    }

    /// <summary>The --test-tray line, as Electron prints it: the top-level rows joined by " | ".</summary>
    public string TestTrayLine()
    {
        if (LastInputs == null) return "mantle: test-tray: the tray was never built";
        var items = TrayModel.Template(LastInputs);
        return "mantle: test-tray: " + string.Join(" | ", items.Select(i => i.Kind == TrayItemKind.Separator ? "---"
            : i.Label + (i.Accelerator != null ? $" [{i.Accelerator}]" : "") + (!i.Enabled ? " (text)" : "") + (i.Submenu != null ? $" ({i.Submenu.Count} inside)" : "")));
    }

    /// <summary>A tray action that failed says so on screen (a balloon from the icon), not only in the log.</summary>
    public void Notice(string title, string content)
    {
        _log($"tray: {title}: {content}");   // the log keeps the exact words
        try { _o.Icon.Balloon(TrayModel.Words(title), TrayModel.Words(content), true); }
        catch (Exception e) { _log($"tray: the balloon could not be shown ({e.Message})"); }
    }

    /// <summary>A chosen row (main.js TRAY_ACTIONS; feature rows are lib/features.js trayItems' click).</summary>
    public void Choose(TrayItem item)
    {
        var id = item.Id ?? "";
        try
        {
            switch (id)
            {
                case "launcher": Run("The launcher did not open", _o.ShowLauncher, "the tray menu", "the launcher is not part of this build yet"); break;
                case "wallpapers": Run("Wallpapers did not open", _o.OpenWallpapers, "the tray menu", "the wallpaper library is not part of this build yet"); break;
                case "settings": _ = Call("settings.open", Via(), "Mantle Settings did not open", "the Settings feature is turned off"); break;
                case "update": _ = Call("updates.show", new JsonObject(), "The update could not be shown", "the updates feature is not running"); break;
                case "retry": _ = Call("shell.reconnect", null, "Mantle could not reconnect to Windows", "reconnecting is not part of this build yet"); break;
                case "restart": _ = Call("shell.restart", Via(), "Mantle did not restart", "restarting is not part of this build yet"); break;
                case "report": _ = ReportFromMenu(); break;
                case "restore": _ = RestoreAsync(); break;
                case "quit":
                    if (_o.Quit == null) Notice("Mantle did not quit", "quitting is not part of this build yet");
                    else _o.Quit("the tray menu");
                    break;
                default:
                    if (id.StartsWith("feature:", StringComparison.Ordinal) && _o.Features != null)
                    {
                        var name = id["feature:".Length..];
                        var a = _o.Features.TrySetEnabled(name, !item.Checked, "the tray menu");
                        if (!a.Ok) Notice($"{item.Label} was not switched {(item.Checked ? "off" : "on")}", a.Reason ?? "no reason was given");
                        Request();
                    }
                    else Notice("That menu item did nothing", $"no action is wired for \"{id}\"");
                    break;
            }
        }
        catch (Exception e) { Notice("That menu item did not work", e.Message); }
    }

    static JsonObject Via() => new() { ["via"] = "the tray menu" };

    void Run(string title, Func<string, string?>? action, string via, string missing)
    {
        if (action == null) { Notice(title, missing); return; }
        string? why;
        try { why = action(via); } catch (Exception e) { why = e.Message; }
        if (why != null) Notice(title, why);
    }

    async Task Call(string command, JsonObject? args, string title, string missing)
    {
        if (!_o.Commands.Has(command)) { Notice(title, missing); return; }
        CommandResult r;
        try { r = await _o.Commands.CallAsync(command, args, "tray"); }
        catch (Exception e) { Notice(title, e.Message); return; }
        if (!r.Ok) { Notice(title, r.Error ?? "no reason was given"); return; }
        // a handler that answers {ok:false, reason} (Electron's shape) failed too
        if (r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
            Notice(title, r.Result.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? why.GetString()! : "no reason was given");
    }

    async Task ReportFromMenu()
    {
        try
        {
            var (ok, why) = await CopyReportAsync("the tray menu");
            if (!ok) Notice("The report was not copied", why ?? "no reason was given");
        }
        catch (Exception e) { Notice("The report was not copied", e.Message); }
    }

    /// <summary>"Copy diagnostic report": onto the clipboard and a balloon that says so. A test run writes it to its
    /// profile instead (never the user's clipboard). Never throws.</summary>
    public async Task<(bool Ok, string? Reason)> CopyReportAsync(string via)
    {
        if (_o.Report == null) return (false, "the diagnostic report is not part of this build yet");
        string text;
        try { text = await _o.Report(via); }
        catch (Exception e) { return (false, $"the report could not be made ({e.Message})"); }
        var lines = text.Split('\n').Length;
        if (_o.Test)
        {
            var w = _o.WriteTestReport == null ? "a test run has nowhere to write the report" : _o.WriteTestReport(text);
            if (w != null) return (false, w);
            _log($"diagnostic report ({lines} lines, asked from {via}) written to the test profile, not the clipboard (test run)");
            return (true, null);
        }
        var copy = _o.CopyText ?? Win32TrayIcon.CopyText;
        string? err;
        try { err = copy(text); } catch (Exception e) { err = e.Message; }
        if (err != null) return (false, err);
        _log($"diagnostic report copied to the clipboard ({lines} lines, asked from {via})");
        try { _o.Icon.Balloon(TrayReport.CopiedTitle, TrayReport.CopiedText, false); } catch (Exception e) { _log($"tray: the balloon could not be shown ({e.Message})"); }
        return (true, null);
    }

    /// <summary>"Put Windows back (keep Mantle open)": pause, stand the supervisor down, run the verified restore,
    /// then show the paused menu. There is deliberately never a "hide the Windows taskbar" item.</summary>
    public async Task RestoreAsync()
    {
        Paused = true;
        try { _o.StandDown?.Invoke("Windows was put back from the tray menu (\"Restore Windows taskbar\")"); }
        catch (Exception e) { _log($"tray: the supervisor did not stand down ({e.Message}); putting Windows back anyway"); }
        try
        {
            if (_o.Restore == null) Notice("Windows was not put back", "putting Windows back is not part of this build yet");
            else await _o.Restore("the tray menu asked for it");
        }
        catch (Exception e) { Notice("Windows was not put back", e.Message); }
        finally { Force(); }
    }

    public void Dispose()
    {
        _batch.Cancel();
        _up = false;
        try { _o.Icon.Dispose(); } catch (Exception e) { _log($"tray: the icon did not go cleanly ({e.Message})"); }
    }
}
