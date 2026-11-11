using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.Media;

/// <summary>
/// The media commands with Electron's names (main.js COMMANDS media.get, media.select, media.control), registered through
/// the generated catalog: <c>MediaCommands.Register(bus, new MediaCommands(service))</c>. One more, native only:
/// <c>media.card {open}</c>, the bar telling the host the card is open (main.js read its own barPopover.key; the
/// native pop-ups live on the UI thread), so position-only changes are read only while someone looks.
/// </summary>
public sealed class MediaCommands
{
    readonly MediaService _svc;
    public MediaCommands(MediaService svc) { _svc = svc; }

    public static IDisposable Register(ICommandBus bus, MediaCommands owner) => Mantle.Generated.CommandCatalog.RegisterCommands(bus, owner);

    static JsonElement Prop(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? v : default;

    /// media.get: a fresh read ("the bar asked"), published and answered.
    [Command("media.get")]
    public async ValueTask<CommandResult> Get(CommandCall call)
    {
        var p = await _svc.Push("the bar asked").ConfigureAwait(true);
        return CommandResult.Success(MediaService.El(p ?? new JsonObject { ["ok"] = false, ["reason"] = "what is playing could not be read" }));
    }

    /// media.select {aumid|null}: the card's source list.
    [Command("media.select")]
    public async ValueTask<CommandResult> Select(CommandCall call)
    {
        var a = Prop(call.Args, "aumid");
        string? aumid = a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } s ? s : null;
        var p = await _svc.Select(aumid).ConfigureAwait(true);
        return CommandResult.Success(MediaService.El(p ?? new JsonObject { ["ok"] = false, ["reason"] = "what is playing could not be read" }));
    }

    /// media.control {aumid, action, positionMs?}: a refusal is Fail(reason), as the bar reads a rejected call.
    [Command("media.control")]
    public async ValueTask<CommandResult> Control(CommandCall call)
    {
        var act = Prop(call.Args, "action");
        var au = Prop(call.Args, "aumid");
        var pos = Prop(call.Args, "positionMs");
        var r = await _svc.Control(act.ValueKind == JsonValueKind.String ? act.GetString() ?? "" : "",
            au.ValueKind == JsonValueKind.String ? au.GetString() : null,
            pos.ValueKind == JsonValueKind.Number ? pos.GetDouble() : null).ConfigureAwait(true);
        bool ok = !(r["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b);
        return ok ? CommandResult.Success(MediaService.El(r)) : CommandResult.Fail(MediaService.Str(r["reason"]) ?? "the media button did nothing, and no reason was given");
    }

    /// media.card {open}: the card opened or closed (native only, see the summary).
    [Command("media.card")]
    public ValueTask<CommandResult> Card(CommandCall call)
    {
        _svc.CardOpen = Prop(call.Args, "open").ValueKind == JsonValueKind.True;
        return ValueTask.FromResult(CommandResult.Success(MediaService.El(new JsonObject { ["ok"] = true, ["open"] = _svc.CardOpen })));
    }
}
