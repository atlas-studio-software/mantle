using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.Desktop;

/// <summary>What the desktop surface hears: hide the widgets under windows, or not (the feature's switch).</summary>
public sealed record WidgetHideOn(bool On);

/// <summary>
/// lib/features/widgethide.js: "Hide desktop widgets under windows", default on, its switch in the tray (and Mantle
/// Settings, which lists every feature). The watch is the desktop surface's own (CoverWatch, in-process WinEvents): no
/// helper round trip, so Electron's "the helper is out of date" case does not exist here. This feature holds the switch
/// and the health: the surface reports the watch's trouble through <c>widgethide.health {ok, reason}</c>, which puts a
/// row in the strip and the tray in words. It fails open: whenever the answer is unknown, every widget shows.
/// </summary>
[Feature("widgethide", "Hide desktop widgets under windows", true)]
public sealed class WidgetHideFeature : IFeature
{
    IFeatureContext _ctx = null!;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        ctx.Replay("widgethide.on", () => new WidgetHideOn(ctx.Enabled));
        ctx.OnEnabled(on => {
            if (!on) ctx.Health(true, WidgetCover.Off);
            ctx.Broadcast("widgethide.on", new WidgetHideOn(on));
            ctx.Log(on ? "switched on: the desktop widgets step aside under windows" : $"{WidgetCover.Off} (switched off in the tray)");
        });
        ctx.Command("health", c => Health(c.Args));
        if (!ctx.Enabled) ctx.Health(true, WidgetCover.Off);
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        try { _ctx.Broadcast("widgethide.on", new WidgetHideOn(false)); } catch { /* stopped */ }
        return ValueTask.CompletedTask;
    }

    ValueTask<CommandResult> Health(JsonElement args)
    {
        bool ok = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
        string? reason = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (!ok && string.IsNullOrEmpty(reason)) reason = "the desktop layer reported trouble without a reason, which is itself a bug";
        if (_ctx.Enabled) _ctx.Health(ok, ok ? null : reason);
        return new(CommandResult.Success(El(new JsonObject { ["ok"] = ok })));
    }

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
}
