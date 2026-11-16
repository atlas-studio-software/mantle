using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Run;

namespace Mantle.Core.Features.QuickLinks;

/// <summary>
/// How a quick link or a submenu row runs, pure but for File.Exists / PATH (lib/features/quicklinks.js open, and the
/// power half of lib/launcher-index.js). The page sends only a row id; what runs comes from <see cref="WinXLinks"/> here.
/// </summary>
public static class QuickLinkPlan
{
    /// One way to start something: ShellExecute on <see cref="File"/> (with Verb "runas" for Terminal (Admin)), an exe
    /// with arguments, the desktop toggle, or a launcher item run through <see cref="LauncherLaunch"/>.
    public sealed record Step(string Kind, string? File = null, IReadOnlyList<string>? Args = null, bool Admin = false, LauncherItem? Item = null, string Note = "", string Name = "");

    public const string NoTerminal = "Windows Terminal isn't installed, so Windows PowerShell opened instead.";
    static string Sys32 => Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32");
    static string WinDir => Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";

    /// <summary>The step for a row id, or the refusal words (unknown id, a row that is the page's own).</summary>
    public static (Step? Step, string? Refusal) For(string? linkId, Func<string, string?> onPath)
    {
        var link = WinXLinks.ById(linkId);
        if (link == null || link.Run == null) return (null, WinXLinks.Unknown);
        string name = WinXLinks.Parse(link.Label).Text;
        var run = link.Run;
        switch (run.Kind)
        {
            case "settings":   // Mantle Settings where it claims the page, else Windows Settings (LauncherLaunch says which)
                return (new Step("item", Item: new LauncherItem { Id = "q:" + link.Id, Kind = "setting", Name = name, Path = run.Uri ?? "ms-settings:" }, Name: name), null);
            case "mantle":
            {
                string key = (run.Path ?? "").Replace("mantle:", "");
                string? win = key switch { "taskmgr" => Path.Combine(Sys32, "Taskmgr.exe"), "files" => Path.Combine(WinDir, "explorer.exe"), _ => null };
                return (new Step("item", Item: new LauncherItem { Id = "q:" + link.Id, Kind = "setting", Name = name, Path = run.Path ?? "", MantleApp = key, WindowsPath = win }, Name: name), null);
            }
            case "exec": return (new Step("shell", run.File, Name: name), null);
            case "terminal":
            {
                bool wt = onPath("wt.exe") != null;
                return (new Step("shell", wt ? "wt.exe" : "powershell.exe", Admin: run.Admin, Note: wt ? "" : NoTerminal, Name: name), null);
            }
            case "desktop": return (new Step("desktop", Name: name), null);
            default: return (null, "Mantle doesn't know how to open " + name + ".");
        }
    }

    /// <summary>
    /// A submenu row (launcher-index.js power, POWER_RUN): Sign out shutdown /l, Hibernate shutdown /h, Restart
    /// shutdown /r /t 0, Shut down shutdown /s /t 0 (no /f: an app with unsaved work can still say "wait"), Sleep
    /// SetSuspendState(FALSE, FALSE, FALSE) (never rundll32, which hibernates). Ask-first rows refuse without confirmed.
    /// </summary>
    public static (Step? Step, string? Refusal) Power(string? action, bool confirmed)
    {
        var p = WinXLinks.Power.FirstOrDefault(x => x.Action == action);
        if (p == null) return (null, "there is no power action called \"" + action + "\"");
        string name = WinXLinks.Parse(p.Label).Text;
        if (p.Confirm && !confirmed) return (null, name + " asks first: press its letter twice");
        string exe = Path.Combine(Sys32, "shutdown.exe");
        return action switch
        {
            "signout" => (new Step("exe", exe, ["/l"], Name: name), null),
            "hibernate" => (new Step("exe", exe, ["/h"], Name: name), null),
            "restart" => (new Step("exe", exe, ["/r", "/t", "0"], Name: name), null),
            "shutdown" => (new Step("exe", exe, ["/s", "/t", "0"], Name: name), null),
            "sleep" => (new Step("sleep", Name: name), null),
            _ => (null, "there is no power action called \"" + action + "\""),
        };
    }

    /// <summary>What a dry run says it would do.</summary>
    public static string Would(Step s) => s.Kind switch
    {
        "shell" => "dry run: would run ShellExecute(\"" + s.File + "\")" + (s.Admin ? " as administrator (Windows' UAC prompt)" : "") + (s.Note.Length > 0 ? " — " + s.Note : ""),
        "exe" => "dry run: would run \"" + s.File + "\" " + string.Join(" ", s.Args ?? []),
        "sleep" => "dry run: would call SetSuspendState(FALSE, FALSE, FALSE) (sleep)",
        "desktop" => "dry run: would show the desktop",
        _ => "dry run",
    };
}

/// <summary>
/// [Feature("quicklinks")] (lib/features/quicklinks.js): Win+X opens the launcher Mantle already has in its Quick links
/// mode (no new window); Win+X again over it closes it. <c>quicklinks.open {link}</c> and <c>quicklinks.power {action,
/// confirmed}</c> answer {link, ok, reason?, dryRun?}; in <c>--test</c> every one is a dry run that is logged, not done.
/// Rests during a game (Win+X is Windows' then). Nothing polls: it runs only on Win+X or a row.
/// </summary>
[Feature("quicklinks", "Quick links (Win+X)", true, Chords = new[] { Chord }, Rest = true)]
public sealed class QuickLinksFeature : IFeature
{
    public const string Chord = "Win+X";
    public const string Ready = "Win+X opens the launcher's Quick links";

    /// <summary>
    /// Set by the launcher surface when it is made: toggle Quick links on its own thread (why) and say whether it could.
    /// Null: there is no launcher to open.
    /// </summary>
    public static Func<string, bool>? ToggleLauncher { get; set; }

    /// <summary>Starts a step for real (live only). Tests replace it; a --test run never reaches it.</summary>
    public Func<QuickLinkPlan.Step, LaunchResult>? Starter { get; init; }

    IFeatureContext _ctx = null!;
    bool Dry => _ctx.Flags.Test;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        ctx.Command("open", c => OpenAsync(c.Args));
        ctx.Command("power", c => PowerAsync(c.Args));
        ctx.OnChord(Chord, _ =>
        {
            var t = ToggleLauncher;
            if (t == null) { ctx.Log("Win+X: there is no launcher to open"); return; }
            if (!t("Win+X")) ctx.Health(false, "Win+X cannot open the launcher: its window is gone");
        });
        ctx.Health(true, Ready);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop) => ValueTask.CompletedTask;   // nothing in Windows was changed; nothing polls

    static string? Str(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    async ValueTask<CommandResult> OpenAsync(JsonElement a)
    {
        string? id = Str(a, "link");
        var (step, refusal) = QuickLinkPlan.For(id, LauncherLaunch.OnPath);
        LaunchResult res = step == null ? new LaunchResult(false, refusal!) : step.Kind == "desktop" ? await DesktopAsync().ConfigureAwait(true) : Run(step);
        _ctx.Log($"{id ?? "(no id)"}: {(res.Ok ? (res.DryRun ? "dry run" : "opened") : "refused")}{(res.Reason.Length > 0 ? " (" + res.Reason + ")" : "")}");
        return Answer("link", WinXLinks.ById(id)?.Id ?? id, res);
    }

    ValueTask<CommandResult> PowerAsync(JsonElement a)
    {
        string? action = Str(a, "action");
        bool confirmed = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("confirmed", out var c) && c.ValueKind == JsonValueKind.True;
        var (step, refusal) = QuickLinkPlan.Power(action, confirmed);
        var res = step == null ? new LaunchResult(false, refusal!) : Run(step);
        _ctx.Log($"power {action}: {(res.Ok ? (res.DryRun ? "dry run" : "done") : "refused")}{(res.Reason.Length > 0 ? " (" + res.Reason + ")" : "")}");
        return new(Answer("action", action, res));
    }

    static CommandResult Answer(string key, string? id, LaunchResult res)
    {
        var o = new JsonObject { [key] = id, ["ok"] = res.Ok };
        if (res.Reason.Length > 0) o["reason"] = res.Reason;
        if (res.DryRun) o["dryRun"] = true;
        return CommandResult.Success(JsonDocument.Parse(o.ToJsonString()).RootElement.Clone());
    }

    LaunchResult Run(QuickLinkPlan.Step s)
    {
        if (s.Kind == "item") return LauncherLaunch.Launch(s.Item, Dry);
        if (Dry) return new LaunchResult(true, QuickLinkPlan.Would(s), true);
        if (Starter != null) return Starter(s);
        return Start(s);
    }

    static LaunchResult Start(QuickLinkPlan.Step s)
    {
        try
        {
            if (s.Kind == "sleep")
                return SetSuspendState(false, false, false) ? new LaunchResult(true) : new LaunchResult(false, s.Name + " did not happen: Windows said no (" + Marshal.GetLastPInvokeError() + ")");
            var psi = s.Kind == "shell"
                ? new ProcessStartInfo(s.File!) { UseShellExecute = true, Verb = s.Admin ? "runas" : "" }
                : new ProcessStartInfo(s.File!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var x in s.Args ?? []) psi.ArgumentList.Add(x);
            using var p = Process.Start(psi);
            return new LaunchResult(true, s.Note);
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223)   // the UAC prompt was cancelled
        {
            return new LaunchResult(false, s.Name + " didn't open: the administrator prompt was cancelled.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return new LaunchResult(false, s.Name + " didn't open: " + e.Message);
        }
    }

    async Task<LaunchResult> DesktopAsync()
    {
        if (!_ctx.Platform.Status.Ready) return new LaunchResult(false, "Desktop needs the Mantle helper, and it is not connected right now.", Dry);
        var a = new JsonObject(); if (Dry) a["dryRun"] = true;
        var r = await _ctx.Platform.CallAsync("hotcorners.desktop", a, TimeSpan.FromMilliseconds(3000)).ConfigureAwait(true);
        if (!r.Ok) return new LaunchResult(false, "Desktop didn't toggle: " + r.Error, Dry);
        return new LaunchResult(true, Dry ? "dry run: would show the desktop" : "", Dry);
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate, [MarshalAs(UnmanagedType.U1)] bool force, [MarshalAs(UnmanagedType.U1)] bool wakeupEventsDisabled);
}
