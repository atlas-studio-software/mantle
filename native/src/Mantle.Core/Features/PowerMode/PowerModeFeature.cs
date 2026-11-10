using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.PowerMode;

/// <summary>
/// The power mode switch in quick settings (lib/features/powermode.js): Best power efficiency / Balanced / Best
/// performance, through the sidecar's powermode.get / powermode.set / powermode.plan / powermode.watch. Commands
/// <c>powermode.get</c>, <c>powermode.set {mode}</c>, <c>powermode.plan {plan}</c>; every change the sidecar pushes is
/// the event <c>powermode.state</c> (Electron's <c>powermode</c>: a native feature's events must start with its name).
/// A --test run sends every set and plan with dryRun (the sidecar checks everything and writes nothing) and answers a
/// change it would have made as ok:false "this is a test run, so Windows was not changed", exactly as Electron does.
/// With MANTLE_TEST_QS holding a <c>powermode</c> object, a --test run reads that in place of the sidecar.
/// </summary>
[Feature("powermode", "Power mode switch (quick settings)", true)]
public sealed class PowerModeFeature : IFeature
{
    public static readonly string[] Modes = { "efficiency", "balanced", "performance" };
    public const string Off = "the power mode switch is turned off in Mantle’s tray menu";
    public const string NoAnswer = "Windows did not answer in time. Try again in a moment";
    public const string TestRun = "this is a test run, so Windows was not changed";
    public const string StateEvent = "powermode.state";
    static readonly Regex Guid = new(@"^\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    IFeatureContext _ctx = null!;
    JsonObject? _last;
    JsonObject? _fixture;
    bool _dry;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _dry = ctx.Flags.Test;
        if (_dry && Environment.GetEnvironmentVariable("MANTLE_TEST_QS") is { Length: > 0 } fx) {
            try { _fixture = (JsonNode.Parse(fx) as JsonObject)?["powermode"]?.DeepClone() as JsonObject; } catch (JsonException) { _fixture = null; }
            if (_fixture != null) { _last = _fixture; ctx.Log("a --test run with MANTLE_TEST_QS: the power mode is its made-up reading, never the sidecar's"); }
        }
        ctx.Command("get", _ => Answer(GetAsync()));
        ctx.Command("set", c => Answer(SetAsync(c.Args)));
        ctx.Command("plan", c => Answer(PlanAsync(c.Args)));
        ctx.Replay(StateEvent, () => El(_last ?? new JsonObject { ["ok"] = false, ["mode"] = null, ["reason"] = "the power mode has not been read yet" }));
        if (_fixture == null) {
            ctx.Platform.On("powermode", e => {
                if (Node(e.Body) is not JsonObject s) return;
                s.Remove("ev");
                _last = s;
                ctx.Broadcast(StateEvent, El(s));
            });
            ctx.Platform.OnReady(() => _ = WatchAsync());
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask StopAsync(FeatureStop stop)
    {
        if (_fixture != null) return;
        await _ctx.Platform.CallAsync("powermode.watch", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);
    }

    async Task WatchAsync()
    {
        var w = await _ctx.Platform.CallAsync("powermode.watch", new JsonObject { ["on"] = true }, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
        if (!w.Ok) { _ctx.Health(false, $"power mode changes are not being watched: {w.Error}"); return; }
        var res = Node(w.Result) as JsonObject;
        if (res?["state"] is JsonObject st) _last = (JsonObject)st.DeepClone();
        _ctx.Health(true);
        var s = _last;
        _ctx.Log($"mode={S(s?["mode"])} inForce={S(s?["inForce"])} source={S(s?["source"])} saver={S(s?["saver"])}" +
                 $" settable={S(s?["settable"])} available={S(s?["available"])} plan=\"{S(s?["plan"]?["name"])}\"" +
                 (res?["reason"] is JsonValue r && r.TryGetValue<string>(out var why) ? $" watch: {why}" : ""));
    }

    async ValueTask<JsonObject> ReadAsync()
    {
        if (_fixture != null) return (JsonObject)_fixture.DeepClone();
        var r = await _ctx.Platform.CallAsync("powermode.get", new JsonObject(), TimeSpan.FromMilliseconds(3000)).ConfigureAwait(true);
        if (!r.Ok) {
            _ctx.Log($"get failed: {r.Error}");
            return new JsonObject { ["ok"] = false, ["mode"] = null, ["available"] = false, ["reason"] = "Windows did not answer in time, so Mantle could not read the power mode" };
        }
        _last = Node(r.Result) as JsonObject ?? new JsonObject { ["ok"] = false, ["reason"] = "the power mode came back empty" };
        return (JsonObject)_last.DeepClone();
    }

    async ValueTask<JsonObject> GetAsync()
    {
        if (!_ctx.Enabled) return new JsonObject { ["ok"] = false, ["mode"] = null, ["available"] = false, ["reason"] = Off };
        return await ReadAsync().ConfigureAwait(true);
    }

    JsonNode? Last() => _last?.DeepClone();

    async ValueTask<JsonObject> SetAsync(JsonElement a)
    {
        string? mode = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        if (mode == null || !Modes.Contains(mode)) return new JsonObject { ["ok"] = false, ["mode"] = null, ["state"] = Last(), ["reason"] = "that is not a power mode Windows has" };
        if (!_ctx.Enabled) return new JsonObject { ["ok"] = false, ["mode"] = mode, ["state"] = Last(), ["reason"] = Off };
        JsonObject res;
        if (_fixture != null) {
            bool same = S(_fixture["mode"]) == mode;
            res = new JsonObject { ["ok"] = true, ["dryRun"] = true, ["mode"] = mode, ["same"] = same, ["state"] = _fixture.DeepClone(), ["reason"] = null };
        } else {
            var args = new JsonObject { ["mode"] = mode };
            if (_dry) args["dryRun"] = true;
            var r = await _ctx.Platform.CallAsync("powermode.set", args, TimeSpan.FromMilliseconds(4000)).ConfigureAwait(true);
            if (!r.Ok) { _ctx.Log($"set {mode} failed: {r.Error}"); return new JsonObject { ["ok"] = false, ["mode"] = mode, ["state"] = await ReadAsync().ConfigureAwait(true), ["reason"] = NoAnswer }; }
            res = Node(r.Result) as JsonObject ?? new JsonObject { ["ok"] = false, ["reason"] = "the sidecar answered with nothing" };
        }
        res = Settle(res);
        _ctx.Log($"set {mode}{(_dry ? " (dry run)" : "")}: {(QsOk(res) ? "taken" : "refused")}{(res["reason"] is JsonValue rv && rv.TryGetValue<string>(out var why) ? $" ({why})" : "")}");
        return res;
    }

    async ValueTask<JsonObject> PlanAsync(JsonElement a)
    {
        string? plan = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("plan", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        if (plan == null || !Guid.IsMatch(plan)) return new JsonObject { ["ok"] = false, ["plan"] = null, ["state"] = Last(), ["reason"] = "that is not a power plan on this PC" };
        if (!_ctx.Enabled) return new JsonObject { ["ok"] = false, ["plan"] = plan, ["state"] = Last(), ["reason"] = Off };
        JsonObject res;
        if (_fixture != null) res = new JsonObject { ["ok"] = true, ["dryRun"] = true, ["same"] = false, ["plan"] = plan, ["state"] = _fixture.DeepClone() };
        else {
            var args = new JsonObject { ["plan"] = plan };
            if (_dry) args["dryRun"] = true;
            var r = await _ctx.Platform.CallAsync("powermode.plan", args, TimeSpan.FromMilliseconds(6000)).ConfigureAwait(true);
            if (!r.Ok) { _ctx.Log($"plan failed: {r.Error}"); return new JsonObject { ["ok"] = false, ["plan"] = plan, ["state"] = await ReadAsync().ConfigureAwait(true), ["reason"] = NoAnswer }; }
            res = Node(r.Result) as JsonObject ?? new JsonObject { ["ok"] = false, ["reason"] = "the sidecar answered with nothing" };
        }
        res = Settle(res);
        string name = S(res["state"]?["plan"]?["name"]) is { Length: > 0 } n && n != "null" ? n : plan;
        _ctx.Log($"plan{(_dry ? " (dry run)" : "")}: {(QsOk(res) ? $"taken (now \"{name}\")" : "refused")}{(res["reason"] is JsonValue rv && rv.TryGetValue<string>(out var why) ? $" ({why})" : "")}");
        return res;
    }

    /// powermode.js answer(): the state is kept, and a dry run that WOULD have changed something is not "ok" (a caller
    /// must not read ok as "Windows changed"): it says what would have happened instead.
    JsonObject Settle(JsonObject res)
    {
        if (res["state"] is JsonObject st) _last = (JsonObject)st.DeepClone();
        if (QsTrue(res["dryRun"]) && QsOk(res) && !QsTrue(res["same"])) { res["ok"] = false; res["reason"] = TestRun; }
        return res;
    }

    static bool QsOk(JsonObject o) => !(o["ok"] is JsonValue v && v.TryGetValue<bool>(out var b) && !b);
    static bool QsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    static string S(JsonNode? n) => n == null ? "null" : n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n.ToJsonString();

    static ValueTask<CommandResult> Answer(ValueTask<JsonObject> work) => Wrap(work);
    static async ValueTask<CommandResult> Wrap(ValueTask<JsonObject> work) => CommandResult.Success(El(await work.ConfigureAwait(true)));

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
    static JsonNode? Node(JsonElement e) => e.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());
}
