using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.Desktop;

/// <summary>What the desktop surface hears: open the menu at a screen point (physical px) for right-click <c>Seq</c>.</summary>
public sealed record DeskMenuShow(int Seq, int X, int Y, bool Test);
/// <summary>Close the menu (the helper gave the click to Windows, or the feature stopped).</summary>
public sealed record DeskMenuHide(string Why);
/// <summary>The widgets switch (Electron's localStorage <c>mantle.desktop.widgets</c>, now in features\deskmenu.json).</summary>
public sealed record DeskWidgets(bool On);

/// <summary>
/// lib/features/deskmenu.js: Mantle's own desktop right-click menu. The sidecar's DeskClick hook (WH_MOUSE_LL, its own
/// thread) sees a right-click on the EMPTY desktop and says <c>deskmenu {op:open, seq, x, y}</c>; this feature tells the
/// desktop surface (<c>deskmenu.show</c>), which draws the menu and acks (<c>deskmenu.ack {seq}</c>) or nacks with why, so
/// the helper gives the click back to Windows' menu when Mantle does not answer within 1 s. Shift+right-click and a
/// right-click on any real window are never Mantle's.
///
/// <para>A --test run never arms the hook (a dry run, logged): the menu opens only through the test road
/// (<c>--test-surface=show:deskmenu</c>). It also owns the widgets switch, migrated from the page's localStorage.</para>
/// </summary>
[Feature("deskmenu", "Desktop right-click menu (Shift+right-click: Windows' own)", true, Rest = true)]
public sealed class DeskMenuFeature : IFeature
{
    IFeatureContext _ctx = null!;
    bool _armed, _widgetsOn = true;
    int _standGen;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _widgetsOn = !(ctx.Settings.TryGet("widgets", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() == "off");
        ctx.Command("widgets", _ => new(CommandResult.Success(El(new JsonObject { ["on"] = _widgetsOn }))));
        ctx.Command("setWidgets", c => SetWidgetsAsync(c.Args));
        ctx.Command("ack", c => ForwardAsync("deskmenu.ack", c.Args));
        ctx.Command("nack", c => ForwardAsync("deskmenu.nack", c.Args));
        ctx.Replay("deskmenu.widgets", () => new DeskWidgets(_widgetsOn));
        ctx.Platform.On("deskmenu", e => OnHelper(e.Body));
        ctx.OnEnabled(on => _ = ArmAsync(on, on ? "switched on in the tray" : "switched off in the tray"));
        _ = ArmAsync(ctx.Enabled, "Mantle started");
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(FeatureStop stop)
    {
        try { _ctx.Broadcast("deskmenu.hide", new DeskMenuHide(stop.Reason)); } catch { /* stopped */ }
        if (_armed) await ArmAsync(false, stop.Reason).ConfigureAwait(true);
    }

    async Task ArmAsync(bool on, string why)
    {
        if (!on) _ctx.Health(true, "switched off: a right-click on the desktop gives Windows' menu");
        if (_ctx.Flags.Test) {
            _ctx.Log($"dry run (--test): would {(on ? "arm" : "disarm")} the desktop right-click hook (deskmenu.arm {{on:{(on ? "true" : "false")}}}); nothing was sent ({why})");
            return;
        }
        var r = await _ctx.Platform.CallAsync("deskmenu.arm", new JsonObject { ["on"] = on }, TimeSpan.FromMilliseconds(3000)).ConfigureAwait(true);
        _armed = on && r.Ok;
        if (on && !r.Ok) _ctx.Health(false, $"a right-click on the desktop gives Windows' menu: the helper could not watch it ({r.Error})");
        else if (on) _ctx.Health(true);
        _ctx.Log($"the desktop right-click hook is {(on ? (r.Ok ? "armed" : "NOT armed: " + r.Error) : "disarmed")} ({why})");
    }

    void OnHelper(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("op", out var op)) return;
        int seq = e.TryGetProperty("seq", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
        switch (op.GetString()) {
            case "open":
                int x = e.TryGetProperty("x", out var vx) && vx.ValueKind == JsonValueKind.Number ? vx.GetInt32() : 0;
                int y = e.TryGetProperty("y", out var vy) && vy.ValueKind == JsonValueKind.Number ? vy.GetInt32() : 0;
                _ctx.Broadcast("deskmenu.show", new DeskMenuShow(seq, x, y, false));
                break;
            case "fallback": {
                // DESK-124: Mantle did not ack within 1 s (or nacked), so the helper gave the click to Windows' menu; two
                // misses in a row and the helper stands down: the health row says why, and the hook re-arms after 30 s
                var f = DeskMenu.Fallback(Str(e, "why"), Bool(e, "replayed"), Bool(e, "standDown"), Str(e, "standWhy"));
                _ctx.Broadcast("deskmenu.hide", new DeskMenuHide("the helper gave this right-click to Windows"));
                _ctx.Log(f.Line);
                if (f.Health != null) { _ctx.Health(false, f.Health); _ = StandDownAsync(); }
                break;
            }
        }
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    /// The helper stood down: arm again after DeskMenu.StandDownRetryMs (a newer stand-down restarts the wait).
    async Task StandDownAsync()
    {
        int my = ++_standGen;
        try { await Task.Delay(DeskMenu.StandDownRetryMs, _ctx.Stopping).ConfigureAwait(true); }
        catch (OperationCanceledException) { return; }
        if (my != _standGen) return;
        await ArmAsync(_ctx.Enabled, "trying again after standing down").ConfigureAwait(true);
    }

    async ValueTask<CommandResult> ForwardAsync(string verb, JsonElement a)
    {
        if (_ctx.Flags.Test) return CommandResult.Success(El(new JsonObject { ["ok"] = true, ["dryRun"] = true }));
        var args = a.ValueKind == JsonValueKind.Object ? JsonNode.Parse(a.GetRawText()) as JsonObject : new JsonObject();
        var r = await _ctx.Platform.CallAsync(verb, args, TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);
        return r.Ok ? CommandResult.Success(r.Result) : CommandResult.Fail(r.Error ?? "the helper did not answer");
    }

    async ValueTask<CommandResult> SetWidgetsAsync(JsonElement a)
    {
        bool on = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
        var w = await _ctx.Settings.SetAsync("widgets", JsonValue.Create(on ? "on" : "off")).ConfigureAwait(true);
        if (!w.Ok) return CommandResult.Success(El(new JsonObject { ["ok"] = false, ["reason"] = $"the setting could not be saved ({w.Reason})" }));
        _widgetsOn = on;
        _ctx.Log($"the desktop widgets are {(on ? "shown" : "hidden")} (the desktop menu)");
        _ctx.Broadcast("deskmenu.widgets", new DeskWidgets(on));
        return CommandResult.Success(El(new JsonObject { ["ok"] = true, ["on"] = on }));
    }

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
}
