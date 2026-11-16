using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mantle.Core.Commands;
using Mantle.Core.Data;

namespace Mantle.Core.Features.TopBar;

// lib/features/topbar.js's ping test (TOP-139..159): topbar.pingTest {action: start | cancel | state} goes to the
// sidecar (sidecar/PingTest.cs, topbar.pingtest), which runs it on demand only and pushes `pingtest` states; each push
// is forwarded as topbar.pingtest, and a finished run joins the history (PingTestHistory, <data root>\ping-tests.json,
// through the ONE write gate), pushed as topbar.pingtests. topbar.pingTests {action: list | save | rename | delete}.
// A --test fixture run (MANTLE_TEST_TOPBAR) never pings or downloads: PingTestFake plays a made-up run instead.
// Nothing here runs at rest: no timer until a start, and the fake's steps end with its run.
public sealed partial class TopBarFeature
{
    public static readonly string[] PingTestActions = { "start", "cancel", "state" };
    public static readonly string[] PingTestsActions = { "list", "save", "rename", "delete" };
    static readonly Regex TestId = new("^[0-9]{1,16}-[0-9]{1,6}$", RegexOptions.Compiled);

    /// Where the history file lives (App: the run's data root and the ONE write gate). Unset = nowhere: every write refused.
    public sealed record PingPaths(string DataRoot, SafeFile Files);
    static PingPaths? _pingPaths;
    public static void Configure(PingPaths p) => _pingPaths = p;

    PingTestHistory? _pingHist;
    PingTestFake? _pingFake;
    IDisposable? _pingStep;

    static double Epoch() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// checkArgs for pingTest / pingTests: only the checked fields, or why not.
    public static (JsonObject? Args, string? Reason) CheckPingArgs(string verb, JsonElement a)
    {
        string? action = Str(a, "action");
        if (verb == "pingTest")
            return Array.IndexOf(PingTestActions, action) >= 0 ? (new JsonObject { ["action"] = action }, null) : (null, "Mantle does not know that ping test request");
        if (Array.IndexOf(PingTestsActions, action) < 0) return (null, "Mantle does not know that ping test history request");
        if (action == "list") return (new JsonObject { ["action"] = "list" }, null);
        string? id = Str(a, "id");
        if (id == null || !TestId.IsMatch(id)) return (null, "that test could not be identified; open the history again");
        if (action == "delete") return (new JsonObject { ["action"] = "delete", ["id"] = id }, null);
        JsonNode? name = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var nv) ? JsJson.FromElement(nv) : null;
        var n = PingTestHistory.CheckName(name);
        return n.Ok ? (new JsonObject { ["action"] = action, ["id"] = id, ["name"] = n.Name }, null) : (null, n.Reason);
    }

    void InstallPingTest(IFeatureContext ctx)
    {
        _pingHist = _pingPaths is { } p
            ? new PingTestHistory(p.DataRoot, p.Files, ctx.Log)
            : new PingTestHistory(Path.Combine(Path.GetTempPath(), "mantle-pingtest-unconfigured"),
                new SafeFile(WriteGate.Never("the ping test history was given no place to keep its file in this run")), ctx.Log);
        if (_fixture != null) _pingFake = new PingTestFake();

        ctx.Command("pingTest", async c =>
        {
            var (args, why) = CheckPingArgs("pingTest", c.Args);
            if (args == null) { ctx.Log($"refused a page request (pingTest): {why}"); return NoWith(why!); }
            if (_pingFake != null) return Answer(FakePingTest((string)args["action"]!));
            return FromSidecar(await ctx.Platform.CallAsync("topbar.pingtest", args));
        });
        ctx.Command("pingTests", c =>
        {
            var (x, why) = CheckPingArgs("pingTests", c.Args);
            if (x == null) { ctx.Log($"refused a page request (pingTests): {why}"); return new(NoWith(why!)); }
            string action = (string)x["action"]!, id = (string?)x["id"] ?? "";
            var r = action switch
            {
                "list" => new JsonObject { ["ok"] = true },
                "save" => _pingHist!.Save(id, x["name"]),
                "rename" => _pingHist!.Rename(id, x["name"]),
                _ => _pingHist!.Remove(id),
            };
            var history = _pingHist!.View();
            if (action != "list") Broadcast("topbar.pingtests", J((JsonObject)history.DeepClone()));
            r["history"] = history;
            return new(Answer(r));
        });
        ctx.Platform.On("pingtest", e =>
        {
            if (_pingFake != null || _stopped) return;   // a fixture run plays its own; a stray real push is not drawn
            if (JsonNode.Parse(e.Body.GetRawText()) is JsonObject p) OnPingTest(p);
        });
    }

    /// A pingtest state: to the page, and a finished run into the history.
    void OnPingTest(JsonObject p)
    {
        p.Remove("ev");
        Broadcast("topbar.pingtest", J(p));
        if ((string?)p["phase"] == "done" && _pingHist!.Add(p) != null) Broadcast("topbar.pingtests", J(_pingHist.View()));
    }

    JsonObject FakePingTest(string action)
    {
        var f = _pingFake!;
        switch (action)
        {
            case "start":
            {
                var (snap, no) = f.Start(Epoch());
                if (snap == null) return new JsonObject { ["ok"] = false, ["reason"] = no };
                _ctx.Log("ping test: a made-up run started (--test fixture: nothing is pinged or downloaded)");
                OnPingTest((JsonObject)snap.DeepClone());
                FakeNext();
                return snap;
            }
            case "cancel":
            {
                _pingStep?.Dispose(); _pingStep = null;
                var s = f.Cancel(Epoch());
                OnPingTest((JsonObject)s.DeepClone());
                return s;
            }
            default: return f.State();
        }
    }

    void FakeNext()
    {
        _pingStep?.Dispose();
        _pingStep = Later(PingTestFake.StepMs, () =>
        {
            _pingStep = null;
            if (_pingFake?.Step(Epoch()) is not { } s) return;
            OnPingTest(s);
            if (_pingFake.Running) FakeNext();
            else _ctx.Log($"ping test: the made-up run ended ({(string?)s["phase"]})");
        });
    }
}
