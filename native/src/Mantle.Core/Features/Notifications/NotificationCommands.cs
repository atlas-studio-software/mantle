using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.Notifications;

/// <summary>
/// The bar's right-cluster commands with Electron's names (lib/notifications.js COMMANDS, main.js 'tray.list',
/// 'tray.click', 'sys.keys'), registered through the generated catalog:
/// <c>NotificationCommands.Register(bus, new NotificationCommands(centre, area))</c>.
/// notifs.* and sys.keys always answer Success({ok, reason, ...}) as Electron resolves them (the panel words a refusal);
/// tray.click's refusal is Fail(reason), as main threw it.
/// </summary>
public sealed class NotificationCommands
{
    readonly NotificationCentre _c;
    readonly NotificationArea _a;
    public NotificationCommands(NotificationCentre centre, NotificationArea area) { _c = centre; _a = area; }

    public static IDisposable Register(ICommandBus bus, NotificationCommands owner) => Mantle.Generated.CommandCatalog.RegisterCommands(bus, owner);

    static JsonNode? Prop(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) ? JsonNode.Parse(v.GetRawText()) : null;
    static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static CommandResult Ok(JsonNode o) => CommandResult.Success(NotificationCentre.El(o));

    [Command("notifs.get")]
    public async ValueTask<CommandResult> Get(CommandCall call) => Ok(await _c.GetAsync().ConfigureAwait(true));

    [Command("notifs.seen")]
    public CommandResult Seen(CommandCall call) => Ok(_c.Seen(Prop(call.Args, "ids")));

    [Command("notifs.remove")]
    public async ValueTask<CommandResult> Remove(CommandCall call) => Ok(await _c.RemoveAsync(Prop(call.Args, "ids")).ConfigureAwait(true));

    [Command("notifs.clear")]
    public async ValueTask<CommandResult> Clear(CommandCall call) => Ok(await _c.ClearAsync(Str(call.Args, "aumid")).ConfigureAwait(true));

    [Command("notifs.open")]
    public async ValueTask<CommandResult> Open(CommandCall call) => Ok(await _c.OpenAsync(Prop(call.Args, "id")).ConfigureAwait(true));

    [Command("tray.list")]
    public CommandResult TrayList(CommandCall call) => Ok(_a.Now);

    [Command("tray.click")]
    public async ValueTask<CommandResult> TrayClick(CommandCall call)
    {
        var (ok, reason, result) = await _a.ClickAsync(Str(call.Args, "id"), Str(call.Args, "button")).ConfigureAwait(true);
        return ok ? Ok(result ?? new JsonObject { ["ok"] = true }) : CommandResult.Fail(reason ?? "the click did not go through");
    }

    [Command("sys.keys")]
    public async ValueTask<CommandResult> Keys(CommandCall call) => Ok(await _a.KeysAsync(Str(call.Args, "combo")).ConfigureAwait(true));
}
