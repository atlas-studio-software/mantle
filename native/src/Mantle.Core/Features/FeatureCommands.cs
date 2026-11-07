using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.State;

namespace Mantle.Core.Features;

/// <summary>
/// The host's own <c>features.*</c> commands (main.js startFeatures): <c>features.list</c> for Mantle Settings,
/// and in a --test run only <c>features.fire {chord} | {key}</c>, which drives a feature's chord or observed key
/// as if the hook had seen it (a test run registers no real keys). The answers are written by hand (no
/// reflection: Native AOT).
/// </summary>
public sealed class FeatureCommands
{
    public const string Owner = "host";
    readonly FeatureHost _host;
    public FeatureCommands(FeatureHost host) { _host = host ?? throw new ArgumentNullException(nameof(host)); }

    [Command("features.list")]
    public ValueTask<CommandResult> List(CommandCall call) => new(CommandResult.Success(ListJson(_host.List())));

    [Command("features.fire")]
    public ValueTask<CommandResult> Fire(CommandCall call)
    {
        if (!_host.Flags.Test) return new(CommandResult.Fail("features.fire works only in a test run (a live run uses the real keys)"));
        var a = call.Args;
        var chord = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("chord", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var key = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (string.IsNullOrEmpty(chord) && string.IsNullOrEmpty(key)) return new(CommandResult.Fail("features.fire needs {chord} or {key}"));
        var d = !string.IsNullOrEmpty(chord) ? _host.FireChord(chord!, "a page") : _host.FireKey(key!, "a page");
        return new(CommandResult.Success(DeliveryJson(d)));
    }

    /// <summary>Register by hand (until Mantle.Generators emits the registration from the [Command] marks; then
    /// this goes, or the generated one is refused as a duplicate, named). features.fire only in a test run.</summary>
    public IDisposable Register(ICommandBus bus)
    {
        var offs = new List<IDisposable> { bus.Register(new CommandRegistration("features.list", Owner, List)) };
        if (_host.Flags.Test) offs.Add(bus.Register(new CommandRegistration("features.fire", Owner, Fire)));
        return new Offs(offs);
    }

    sealed class Offs : IDisposable
    {
        List<IDisposable>? _l;
        public Offs(List<IDisposable> l) { _l = l; }
        public void Dispose() { var l = _l; _l = null; if (l != null) foreach (var x in l) x.Dispose(); }
    }

    /// <summary>features.list's answer, the shape lib/features.js list() returns (Mantle Settings reads it).</summary>
    public static JsonElement ListJson(IReadOnlyList<FeatureInfo> list)
    {
        var arr = new JsonArray();
        foreach (var f in list) arr.Add(InfoNode(f));
        return JsonDocument.Parse(arr.ToJsonString()).RootElement.Clone();
    }

    static JsonArray Strings(IEnumerable<string> s) => new(s.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    static JsonObject InfoNode(FeatureInfo f) => new()
    {
        ["name"] = f.Name, ["label"] = f.Label, ["chords"] = Strings(f.Chords), ["observe"] = Strings(f.Observe),
        ["enabled"] = f.Enabled, ["ok"] = f.Ok, ["reason"] = f.Reason, ["running"] = f.Running, ["stopped"] = f.Stopped,
        ["resting"] = f.Resting, ["commands"] = Strings(f.Commands), ["loaded"] = f.Loaded, ["keys"] = f.Keys,
        ["keysReason"] = f.KeysReason, ["broken"] = f.Broken,
        ["options"] = new JsonArray(f.Options.Select(o => (JsonNode?)OptionNode(o)).ToArray()),
    };

    static JsonNode? Node(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());

    static JsonObject OptionNode(OptionView v)
    {
        var o = new JsonObject
        {
            ["key"] = v.Key, ["label"] = v.Label, ["help"] = v.Help, ["type"] = v.Type, ["default"] = Node(v.Default),
            ["value"] = Node(v.Value), ["isDefault"] = v.IsDefault, ["note"] = v.Note,
        };
        var opt = v.Option;
        if (opt.Kind == FeatureOptionKind.Choice)
            o["choices"] = new JsonArray(opt.Choices.Select(c => (JsonNode?)new JsonObject { ["value"] = c.Value, ["label"] = string.IsNullOrWhiteSpace(c.Label) ? c.Value : c.Label }).ToArray());
        if (opt.Min != null) o["min"] = opt.Min;
        if (opt.Max != null) o["max"] = opt.Max;
        if (opt.Step != null) o["step"] = opt.Step;
        if (opt.Unit != null) o["unit"] = opt.Unit;
        if (opt.DefaultLabel != null) o["defaultLabel"] = opt.DefaultLabel;
        return o;
    }

    /// <summary>What a fire did, as lib/features.js fireChord / fireKey answer it.</summary>
    public static JsonElement DeliveryJson(KeyDelivery d)
    {
        var o = new JsonObject { ["ok"] = d.Delivered, ["feature"] = d.Feature, ["reason"] = d.Reason, ["handlers"] = d.Handlers };
        if (d.DryRun) o["dryRun"] = true;
        return JsonDocument.Parse(o.ToJsonString()).RootElement.Clone();
    }
}

/// <summary>main.js startFeatures' outer catch and the page reviver's report: things that are not a feature
/// module but show in the same strip and tray as one.</summary>
public static class FeatureSeam
{
    /// <summary>Start the feature seam. If the seam itself throws, the state piece <c>features.seam</c> says
    /// "no feature started: the feature seam threw ..." and Mantle keeps running. Returns the error words, or null.</summary>
    public static string? Start(IShellStateHub state, Action start, Action<string>? log = null)
    {
        try { start(); return null; }
        catch (Exception e)
        {
            log?.Invoke($"the feature seam could not start: {e}");
            var reason = $"no feature started: the feature seam threw {e.Message}";
            state.Set(s => s with { Features = s.Features.SetItem("seam", new FeaturePiece("Features", true, false, reason, false, "", null, false)) });
            return reason;
        }
    }

    /// The three main pages whose dead page (renderer) is reported as a feature-shaped piece.
    public static readonly IReadOnlyList<string> Pages = new[] { "bar", "launcher", "desktop" };

    /// <summary>A main page (bar, launcher, desktop) reports its page as up (ok) or dead, with its reason, as the
    /// piece <c>page.&lt;name&gt;</c> (main.js pageReviver report): the same shape a feature publishes, so a dead
    /// page is said in the strip and the tray, where the user looks. Returns false when nothing changed.</summary>
    public static bool ReportPage(IShellStateHub state, string page, bool ok, string reason)
    {
        if (!Pages.Contains(page)) throw new ArgumentException($"\"{page}\" is not one of Mantle's main pages ({string.Join(", ", Pages)})", nameof(page));
        var key = $"page.{page}";
        var label = page switch { "bar" => "The bar page", "launcher" => "The launcher page", _ => "The desktop page" };
        return state.Set(s => s with { Features = s.Features.SetItem(key, new FeaturePiece(label, true, ok, reason, false, "", null, false)) });
    }
}
