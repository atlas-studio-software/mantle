using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Search;
using Mantle.Core.Logging;

namespace Mantle.Core.Features.Run;

/// <summary>
/// lib/apps.js on the host thread: the index built once and kept 10 minutes (two callers share one in-flight
/// build; <c>refresh</c> forces one), <c>search</c> over it (the last hits kept, so a typed URL can be launched),
/// and <c>launch</c> by id. In <c>--test</c> every launch is a dry run.
/// </summary>
public sealed class LauncherService
{
    readonly LauncherIndexer _indexer;
    readonly ILogSink _log;
    readonly TimeProvider _clock;
    IndexSnapshot _current = IndexSnapshot.Empty;
    Task<IndexSnapshot>? _building;
    List<SearchHit> _lastHits = [];

    public LauncherService(IndexInputs inputs, bool test, ILogSink? log = null)
    {
        _indexer = new LauncherIndexer(inputs);
        _indexer.LateMerged += s => { _current = s; Changed?.Invoke(s); };
        _clock = inputs.Clock;
        Test = test;
        _log = log ?? NullLog.Instance;
    }

    public bool Test { get; }
    public IndexSnapshot Current => _current;
    public event Action<IndexSnapshot>? Changed;

    public Task<IndexSnapshot> IndexAsync(bool refresh = false)
    {
        if (!refresh && _current.Items.Count > 0 && _clock.GetUtcNow() - _current.BuiltAt < LauncherIndexer.Ttl) return Task.FromResult(_current);
        if (_building is { IsCompleted: false }) return _building;
        _building = BuildNow();
        return _building;
    }

    async Task<IndexSnapshot> BuildNow()
    {
        long t0 = Environment.TickCount64;
        IndexSnapshot s;
        try { s = await _indexer.BuildAsync().ConfigureAwait(true); }
        catch (Exception e)
        {
            // the index never takes the launcher down: build() failed becomes an error source (LAU-168)
            s = new IndexSnapshot([], [new IndexSource("apps", 0, "error", "build() failed: " + e.Message)], _clock.GetUtcNow());
        }
        _current = s;
        _log.Line("launcher: index " + s.Items.Count + " rows in " + (Environment.TickCount64 - t0) + " ms (" +
            string.Join(", ", s.Sources.Select(x => x.Name + " " + x.Count + (x.Status == "ok" ? "" : " " + x.Status))) + ")");
        Changed?.Invoke(s);
        return s;
    }

    /// apps.search: at most 12 rows (Electron cuts at 12 again in lib/apps.js).
    public List<SearchHit> Search(string? q)
    {
        var hits = LauncherSearch.Search(q, _current.Items);
        if (hits.Count > LauncherSearch.Limit) hits = hits.Take(LauncherSearch.Limit).ToList();
        _lastHits = hits;
        return hits;
    }

    public LauncherItem? Find(string id) =>
        _current.Items.FirstOrDefault(i => i.Id == id) ?? _lastHits.Select(h => h.Item).FirstOrDefault(i => i.Id == id);

    /// apps.launch {id}: the row from the index, else from the last search's hits (a typed URL is only there).
    public LaunchResult Launch(string id, bool? dryRun = null)
    {
        var item = Find(id);
        if (item == null)
        {
            var why = _current.Sources.FirstOrDefault(s => s.Status != "ok");
            return new LaunchResult(false, why != null ? why.Reason : "nothing in the index has the id \"" + id + "\"");
        }
        bool dry = Test || dryRun == true;
        var r = LauncherLaunch.Launch(item, dry);
        _log.Line("launcher: launch " + item.Kind + " \"" + item.Name + "\": " + (r.Ok ? "ok" : "FAILED") + (r.Reason.Length > 0 ? " (" + r.Reason + ")" : ""));
        return r;
    }

    // ---------------------------------------------------------------- JSON for the command bus

    public static JsonObject ItemJson(LauncherItem it, int? score)
    {
        var o = new JsonObject { ["id"] = it.Id, ["kind"] = it.Kind, ["name"] = it.Name, ["subtitle"] = it.Subtitle, ["path"] = it.Path };
        if (score is int s) o["score"] = s;
        if (it.Secondary) o["secondary"] = true;
        if (it.MantleApp != null) o["mantleApp"] = it.MantleApp;
        if (it.Glyph != null) o["glyph"] = it.Glyph;
        if (it.Confirm != null) o["confirm"] = it.Confirm;
        if (it.IconKey != null) o["iconKey"] = it.IconKey;
        if (it.Icon != null) o["icon"] = it.Icon;
        return o;
    }

    public static JsonArray SourcesJson(IEnumerable<IndexSource> sources)
    {
        var a = new JsonArray();
        foreach (var s in sources) a.Add((JsonNode)new JsonObject { ["name"] = s.Name, ["count"] = s.Count, ["status"] = s.Status, ["reason"] = s.Reason });
        return a;
    }

    internal static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
}

/// <summary>The launcher's host commands, with Electron's names (main.js COMMANDS: apps.index, apps.search, apps.launch).</summary>
public sealed class LauncherCommands
{
    readonly LauncherService _svc;
    public LauncherCommands(LauncherService svc) { _svc = svc; }

    /// Registered by the host: <c>LauncherCommands.Register(bus, new LauncherCommands(service))</c>.
    public static IDisposable Register(ICommandBus bus, LauncherCommands owner) => Mantle.Generated.CommandCatalog.RegisterCommands(bus, owner);

    static string? Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Flag(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// apps.index {refresh?}: the per-source counts and reasons (the rows stay on the host).
    [Command("apps.index")]
    public async ValueTask<CommandResult> Index(CommandCall call)
    {
        var s = await _svc.IndexAsync(Flag(call.Args, "refresh")).ConfigureAwait(true);
        return CommandResult.Success(LauncherService.El(new JsonObject { ["count"] = s.Items.Count, ["sources"] = LauncherService.SourcesJson(s.Sources) }));
    }

    /// apps.search {q}: up to 12 rows with their scores, and the sources (so the footer can say what is missing).
    [Command("apps.search")]
    public async ValueTask<CommandResult> SearchCmd(CommandCall call)
    {
        if (_svc.Current.Items.Count == 0) await _svc.IndexAsync().ConfigureAwait(true);
        var hits = _svc.Search(Str(call.Args, "q"));
        var rows = new JsonArray();
        foreach (var h in hits) rows.Add((JsonNode)LauncherService.ItemJson(h.Item, h.Score));   // the JsonNode overload: Add<T> is not AOT-safe
        return CommandResult.Success(LauncherService.El(new JsonObject { ["items"] = rows, ["sources"] = LauncherService.SourcesJson(_svc.Current.Sources) }));
    }

    /// apps.launch {id}: runs it (a dry run in --test) and answers ok + reason.
    [Command("apps.launch")]
    public ValueTask<CommandResult> LaunchCmd(CommandCall call)
    {
        string? id = Str(call.Args, "id");
        if (string.IsNullOrEmpty(id)) return new(CommandResult.Fail("apps.launch needs an id"));
        var r = _svc.Launch(id, Flag(call.Args, "dryRun") ? true : null);
        var o = new JsonObject { ["ok"] = r.Ok, ["reason"] = r.Reason, ["dryRun"] = r.DryRun };
        return new(r.Ok ? CommandResult.Success(LauncherService.El(o)) : CommandResult.Fail(r.Reason));
    }
}
