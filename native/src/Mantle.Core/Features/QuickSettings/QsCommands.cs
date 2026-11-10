using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.QuickSettings;

/// <summary>
/// The quick-settings host commands, with Electron's names (main.js COMMANDS: qs.get, qs.set, qs.watch,
/// qs.audio.sessions, qs.audio.session), registered through the generated catalog:
/// <c>QsCommands.Register(bus, new QsCommands(service))</c>.
/// </summary>
/// <remarks>
/// The answer's shape, against main.js: a refused or unknown set is <see cref="CommandResult.Fail"/> with its reason
/// (Electron resolved {ok:false, reason, state}); the read-back of a refused switch still reaches the panel through the
/// <c>"qs"</c> event, and the OSD's per-app rows already read a refusal as Ok=false + Error (osd-toasts lane).
/// </remarks>
public sealed class QsCommands
{
    readonly QsService _svc;
    public QsCommands(QsService svc) { _svc = svc; }

    public static IDisposable Register(ICommandBus bus, QsCommands owner) => Mantle.Generated.CommandCatalog.RegisterCommands(bus, owner);

    static JsonElement Prop(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? v : default;
    static string? Str(JsonElement a, string name) => Prop(a, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    /// qs.get: the six parts (cached once all were read; the open panel's watch fills in what moved).
    [Command("qs.get")]
    public async ValueTask<CommandResult> Get(CommandCall call)
        => CommandResult.Success(QsService.El(await _svc.GetAsync().ConfigureAwait(true)));

    /// qs.set {part, value}: the reply is the part read back; a --test run's set is a dry run (dryRun: true).
    [Command("qs.set")]
    public async ValueTask<CommandResult> Set(CommandCall call)
    {
        var r = await _svc.SetAsync(Str(call.Args, "part") ?? "", Prop(call.Args, "value")).ConfigureAwait(true);
        return r.Ok ? CommandResult.Success(QsService.El(r.Json())) : CommandResult.Fail(r.Reason ?? $"{r.Part} was not changed, and no reason was given");
    }

    /// qs.watch {on}: the sidecar's watch while the panel is open.
    [Command("qs.watch")]
    public async ValueTask<CommandResult> Watch(CommandCall call)
    {
        bool on = Prop(call.Args, "on").ValueKind == JsonValueKind.True;
        var r = await _svc.WatchAsync(on, on ? $"{call.Caller} opened the panel" : $"{call.Caller} closed the panel").ConfigureAwait(true);
        return CommandResult.Success(QsService.El(r));
    }

    /// qs.audio.sessions: every app playing sound, each row with its key, name, level and mute.
    [Command("qs.audio.sessions")]
    public async ValueTask<CommandResult> Sessions(CommandCall call)
    {
        var r = await _svc.AudioSessionsAsync().ConfigureAwait(true);
        return QsParts.IsOk(r) ? CommandResult.Success(QsService.El(r)) : CommandResult.Fail(QsParts.Str(r["reason"]) ?? "the apps playing sound cannot be read");
    }

    // ---- one level down (main.js qsMore): the answer is always {ok, reason, ...} with the sidecar's own ok/reason, never
    // a Fail, so the bus's failure log never holds a call's arguments (qs.wifi.connect's carry the password)

    static CommandResult Reply(JsonObject r) => CommandResult.Success(QsService.El(r));

    /// qs.wifi.list: the networks Windows knows of, saved first, then by signal.
    [Command("qs.wifi.list")]
    public async ValueTask<CommandResult> WifiList(CommandCall call) => Reply(await _svc.WifiListAsync().ConfigureAwait(true));

    /// qs.wifi.scan: a fresh scan, then the list.
    [Command("qs.wifi.scan")]
    public async ValueTask<CommandResult> WifiScan(CommandCall call) => Reply(await _svc.WifiScanAsync().ConfigureAwait(true));

    /// qs.wifi.connect {ssid | hex, password?, auto}: a dry run in --test; the password is never logged or kept.
    [Command("qs.wifi.connect")]
    public async ValueTask<CommandResult> WifiConnect(CommandCall call) => Reply(await _svc.WifiConnectAsync(call.Args).ConfigureAwait(true));

    [Command("qs.wifi.disconnect")]
    public async ValueTask<CommandResult> WifiDisconnect(CommandCall call) => Reply(await _svc.WifiDisconnectAsync().ConfigureAwait(true));

    /// qs.audio.devices: the active playback devices, the default one checked.
    [Command("qs.audio.devices")]
    public async ValueTask<CommandResult> AudioDevices(CommandCall call) => Reply(await _svc.AudioDevicesAsync().ConfigureAwait(true));

    /// qs.audio.default {device}: the output for all three roles (a dry run in --test).
    [Command("qs.audio.default")]
    public async ValueTask<CommandResult> AudioDefault(CommandCall call) => Reply(await _svc.AudioDefaultAsync(call.Args).ConfigureAwait(true));

    /// qs.bt.list: the paired devices.
    [Command("qs.bt.list")]
    public async ValueTask<CommandResult> BtList(CommandCall call) => Reply(await _svc.BtListAsync().ConfigureAwait(true));

    /// qs.bt.watch {on}: the sidecar's device watch while the Bluetooth view is open (pushes: "qs.btdevices").
    [Command("qs.bt.watch")]
    public async ValueTask<CommandResult> BtWatch(CommandCall call) => Reply(await _svc.BtWatchAsync(Prop(call.Args, "on").ValueKind == JsonValueKind.True).ConfigureAwait(true));

    [Command("qs.bt.connect")]
    public async ValueTask<CommandResult> BtConnect(CommandCall call) => Reply(await _svc.BtToggleAsync("connect", call.Args).ConfigureAwait(true));

    [Command("qs.bt.disconnect")]
    public async ValueTask<CommandResult> BtDisconnect(CommandCall call) => Reply(await _svc.BtToggleAsync("disconnect", call.Args).ConfigureAwait(true));

    /// qs.audio.session {key, level?, muted?}: one app's volume (a dry run in --test: logged, never sent).
    [Command("qs.audio.session")]
    public async ValueTask<CommandResult> Session(CommandCall call)
    {
        var r = await _svc.AudioSessionAsync(call.Args).ConfigureAwait(true);
        return QsParts.IsOk(r) ? CommandResult.Success(QsService.El(r)) : CommandResult.Fail(QsParts.Str(r["reason"]) ?? "that app's volume could not be changed");
    }
}
