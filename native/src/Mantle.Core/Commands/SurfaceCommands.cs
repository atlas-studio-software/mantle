using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Lifecycle;

namespace Mantle.Core.Commands;

/// <summary>
/// <c>surface.show</c>, <c>surface.hide</c>, <c>surface.toggle</c> and <c>surface.list</c>: the host's road to the
/// surfaces made from Mantle.Generated.SurfaceCatalog. The arguments are the surface's name, as
/// <c>{ "name": "launcher" }</c> or the bare string <c>"launcher"</c>, and an optional <c>"why"</c> for the log
/// (else the caller's name). Marked [Command]; registered through the generated catalog. Host thread.
/// </summary>
public sealed class SurfaceCommands
{
    public const string Owner = "surfaces";
    readonly ISurfaceControl _c;

    public SurfaceCommands(ISurfaceControl control) { _c = control; }

    [Command("surface.show")]
    public ValueTask<CommandResult> Show(CommandCall call) => Run("show", call);

    [Command("surface.hide")]
    public ValueTask<CommandResult> Hide(CommandCall call) => Run("hide", call);

    [Command("surface.toggle")]
    public ValueTask<CommandResult> Toggle(CommandCall call) => Run("toggle", call);

    [Command("surface.list")]
    public async ValueTask<CommandResult> List(CommandCall call)
    {
        var all = await _c.ListAsync().ConfigureAwait(true);
        var a = new JsonArray();
        foreach (var s in all)
            a.Add((JsonNode)new JsonObject {
                ["name"] = s.Name, ["atStart"] = s.AtStart, ["phase"] = s.Phase.ToString().ToLowerInvariant(), ["shown"] = s.Shown, ["reason"] = s.Reason,
            });
        return Answer(new JsonObject { ["surfaces"] = a });
    }

    /// The surface's name from the arguments, or null.
    public static string? NameOf(JsonElement args)
    {
        string? n = args.ValueKind switch {
            JsonValueKind.String => args.GetString(),
            JsonValueKind.Object when args.TryGetProperty("name", out var v) && v.ValueKind == JsonValueKind.String => v.GetString(),
            _ => null,
        };
        n = n?.Trim();
        return string.IsNullOrEmpty(n) ? null : n;
    }

    static string? WhyOf(JsonElement args)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty("why", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } w
            ? (w.Length > 80 ? w[..80] : w) : null;

    async ValueTask<CommandResult> Run(string verb, CommandCall call)
    {
        string? name = NameOf(call.Args);
        if (name == null) return CommandResult.Fail($"surface.{verb} needs the surface's name, like {{ \"name\": \"launcher\" }}");
        if (name.Length > 64 || !name.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            return CommandResult.Fail($"surface.{verb}: a surface name is lower-case letters, digits and dashes (like \"launcher\"), and this was not one");
        var a = await _c.RunAsync(verb, name, WhyOf(call.Args) ?? call.Caller).ConfigureAwait(true);
        if (!a.Ok) return CommandResult.Fail(a.Said);
        return Answer(new JsonObject { ["name"] = a.Name, ["shown"] = a.Shown, ["said"] = a.Said });
    }

    static CommandResult Answer(JsonObject o)
    {
        using var d = JsonDocument.Parse(o.ToJsonString());
        return CommandResult.Success(d.RootElement.Clone());
    }
}
