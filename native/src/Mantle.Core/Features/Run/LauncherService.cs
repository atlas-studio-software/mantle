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
    volatile IndexSnapshot _current = IndexSnapshot.Empty;   // swapped whole; the launcher's UI thread reads it (Search)
    Task<IndexSnapshot>? _building;
    volatile List<SearchHit> _lastHits = [];
    // lib/launcher-index.js iconCache: path (lower case) -> data:image/png URL, or null for a miss. Written on the host
    // thread as answers land, read by Search on any thread (a ConcurrentDictionary: no torn reads).
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);
    Task? _filling;
    string? _iconsSaid;

    /// <summary>
    /// main.js makeIconResolver: a path (a .lnk, or shell:AppsFolder\&lt;aumid&gt;) to a <c>data:image/png</c> URL, or null.
    /// Unset: no icons, and the apps source says "icons unavailable: no icon resolver was passed to build()" (as Electron's).
    /// </summary>
    public Func<string, ValueTask<(string? Icon, string? Reason)>>? IconSource { get; set; }
    /// Icons asked at once (launcher-index.js ICON_BATCH).
    public const int IconBatch = 8;
    /// Raised (on the host thread) when a fill finished: how many rows have a picture now.
    public event Action<int>? IconsFilled;

    public LauncherService(IndexInputs inputs, bool test, ILogSink? log = null)
    {
        _indexer = new LauncherIndexer(inputs);
        _indexer.LateMerged += s => { _current = s = WithIconSource(s); if (IconSource != null) _filling = FillIcons(s); Changed?.Invoke(s); };
        _clock = inputs.Clock;
        Test = test;
        _log = log ?? NullLog.Instance;
    }

    public bool Test { get; }
    public IndexSnapshot Current => _current;
    public event Action<IndexSnapshot>? Changed;

    public Task<IndexSnapshot> IndexAsync(bool refresh = false)
    {
        if (!refresh && _current.Items.Count > 0 && _clock.GetUtcNow() - _current.BuiltAt < LauncherIndexer.Ttl)
        {
            // an open re-asks the rows still without a picture (the sidecar was starting, or late Store rows); the
            // resolver answers its own known misses at once, so this costs nothing when there is nothing new
            if (IconSource != null && _filling is not { IsCompleted: false }) _filling = FillIcons(_current);
            return Task.FromResult(_current);
        }
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
        _current = s = WithIconSource(s);
        if (IconSource != null) _filling = FillIcons(s);
        _log.Line("launcher: index " + s.Items.Count + " rows in " + (Environment.TickCount64 - t0) + " ms (" +
            string.Join(", ", s.Sources.Select(x => x.Name + " " + x.Count + (x.Status == "ok" ? "" : " " + x.Status))) + ")");
        Changed?.Invoke(s);
        return s;
    }

    /// apps.search: at most 12 rows (Electron cuts at 12 again in lib/apps.js), each with its icon when one is known.
    /// Safe on any thread: it reads one snapshot and the icon map, and swaps _lastHits whole.
    public List<SearchHit> Search(string? q)
    {
        var hits = LauncherSearch.Search(q, _current.Items);
        if (hits.Count > LauncherSearch.Limit) hits = hits.Take(LauncherSearch.Limit).ToList();
        if (!_icons.IsEmpty)
            for (int i = 0; i < hits.Count; i++)
                if (hits[i].Item.Icon == null && hits[i].Item.Kind == "app" && _icons.TryGetValue(hits[i].Item.Path, out var url) && url != null)
                    hits[i] = hits[i] with { Item = hits[i].Item with { Icon = url } };
        _lastHits = hits;
        return hits;
    }

    /// A finished icon fill (tests): the task of the last one started, or a done task.
    public Task IconsSettled => _filling ?? Task.CompletedTask;

    /// <summary>
    /// fillIcons(): every app row that is not Mantle's own (a .lnk, or a Store app's shell:AppsFolder path) is asked
    /// once, 8 at a time; a known path is not asked again (the resolver re-checks a stale one itself). Unlike Electron's
    /// build(), the rows do NOT wait for the icons (Electron waits up to 4 s): a row shows its letter tile until its
    /// picture is in, and picks it up on the next search. When it is done the apps source's reason says what failed.
    /// </summary>
    async Task FillIcons(IndexSnapshot s)
    {
        var src = IconSource!;
        long t0 = Environment.TickCount64;
        var todo = s.Items.Where(i => i.Kind == "app" && i.MantleApp == null && i.Icon == null && i.Path.Length > 0 && !_icons.ContainsKey(i.Path) && !i.Path.StartsWith("mantle:", StringComparison.Ordinal))
            .Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string? firstFail = null;
        int cursor = 0;
        async Task Worker()
        {
            while (true)
            {
                int at = Interlocked.Increment(ref cursor) - 1;
                if (at >= todo.Count) return;
                string p = todo[at];
                (string? Icon, string? Reason) r;
                try { r = await src(p).ConfigureAwait(true); }
                catch (Exception e) { r = (null, e.Message); }
                if (r.Icon != null) _icons[p] = r.Icon;   // a miss is not kept here: the resolver keeps (and re-checks) its own
                if (r.Icon == null && r.Reason != null && r.Reason.Contains("cannot be asked", StringComparison.Ordinal)) firstFail ??= r.Reason;
            }
        }
        await Task.WhenAll(Enumerable.Range(0, Math.Min(IconBatch, Math.Max(1, todo.Count))).Select(_ => Worker())).ConfigureAwait(true);
        int with = s.Items.Count(i => i.Kind == "app" && (i.Icon != null || (_icons.TryGetValue(i.Path, out var u) && u != null)));
        int apps = s.Items.Count(i => i.Kind == "app");
        // the apps source: "icons unavailable: <why>" only when the sidecar could not be asked (Electron's G.iconFail)
        string part = firstFail != null ? "icons unavailable: " + firstFail : "";
        if (ReferenceEquals(_current, s) || _current.BuiltAt == s.BuiltAt)
        {
            var cur = _current;
            var sources = cur.Sources.Select(x => x.Name != "apps" ? x : x with { Reason = ReplaceIconsPart(x.Reason, part) }).ToList();
            _current = cur with { Sources = sources };
        }
        string said = with + "/" + apps + "/" + firstFail;
        if (said != _iconsSaid) { _iconsSaid = said; _log.Line($"launcher: icons for {with} of {apps} app row(s) ({todo.Count} asked in {Environment.TickCount64 - t0} ms" + (firstFail != null ? "; " + firstFail : "") + ")"); }
        IconsFilled?.Invoke(with);
    }

    /// The sidecar's calls for an IconResolver (TaskJson.From's shape: a failed call throws, so the resolver records why).
    public static Mantle.Core.Features.WindowList.SidecarCall SidecarOf(Mantle.Core.Platform.IPlatform p) => async (verb, args, timeout) =>
    {
        var r = await p.CallAsync(verb, args, timeout).ConfigureAwait(true);
        if (!r.Ok) throw new Mantle.Core.Features.WindowList.PlatformCallException(r.Error ?? "the platform did not say why");
        return r.Result;
    };

    /// With a resolver, the indexer's "no icon resolver was passed" is not true: it goes until the fill says otherwise.
    IndexSnapshot WithIconSource(IndexSnapshot s) => IconSource == null ? s
        : s with { Sources = s.Sources.Select(x => x.Name != "apps" ? x : x with { Reason = ReplaceIconsPart(x.Reason, "") }).ToList() };

    static string ReplaceIconsPart(string reason, string part)
    {
        var parts = reason.Split("; ").Where(x => x.Length > 0 && !x.StartsWith("icons unavailable: ", StringComparison.Ordinal)).ToList();
        if (part.Length > 0) parts.Add(part);
        return string.Join("; ", parts);
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
