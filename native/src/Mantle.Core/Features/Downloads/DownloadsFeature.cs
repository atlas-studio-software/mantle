using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.Downloads;

/// <summary>
/// [Feature("downloads")] (lib/features/downloads.js): the Downloads shelf's service. The newest 12 items of the user's
/// Downloads folder (FOLDERID_Downloads, so a folder moved into OneDrive is followed), newest arrival first, partials and
/// hidden/system files left out (DownloadsList is the pure half); a dot while something arrived since the shelf was last
/// opened (setting seenAt); open / reveal / the folder; rows addressed by opaque tokens, never by path.
///
/// WHAT IT COSTS: ONE FileSystemWatcher on the folder (ReadDirectoryChangesW: nothing runs while nothing changes). An event
/// re-reads after 700 ms of quiet, at most every 4 s while events keep coming; a browser writing its .crdownload/.part is
/// ignored. No poll: the only repeating timer is the 2-minute retry while the folder is MISSING. Rest = true: a game
/// closes the watch.
///
/// Native reads the folder in-process (attributes included, off the host thread), so there is no helper round trip and
/// no "simpler list" fallback where hidden files could show (Electron's BAR-900 path), and the first read is at start,
/// not after the helper is up or 10 s (BAR-901).
///
/// PRIVACY: the log gets counts only; no reason, log line, health text or command argument carries a name or a path.
/// --test: open / reveal / folder are DRY RUNS; the folder is MANTLE_DOWNLOADS_DIR only (never the user's real Downloads:
/// without it a test run reads no folder and says so).
/// </summary>
[Feature("downloads", "Downloads shelf (bar)", true, Rest = true)]
public sealed class DownloadsFeature : IFeature
{
    public const string StateEvent = "downloads.state";
    public const string DirEnv = "MANTLE_DOWNLOADS_DIR";
    public const string Off = "the Downloads shelf is off in Mantle’s tray menu";
    public const int ShelfMax = 12, FetchMax = 60;
    public const int DebounceMs = 700, MaxWaitMs = 4000, RetryMsDefault = 120000;
    public const string WatchWords = "new downloads show up when you open this panel, not the moment they finish";
    public const string NoTestDir = "test run: MANTLE_DOWNLOADS_DIR is not set, so the shelf reads no folder (a test never reads the real Downloads folder)";
    const string Gone = "that row is out of date (the list changed); it has been refreshed";
    const string NoLonger = "it is no longer in Downloads (moved or deleted); the list has been refreshed";

    /// <summary>What the feature is given (tests: a temp folder, a fake watch, a manual clock).</summary>
    public sealed record Host(Func<string?>? Folder = null, HostClock? Clock = null, Watcher? Watch = null,
        Func<string, string?>? Open = null, Func<string, string?>? Reveal = null, int? RetryMs = null, Func<long>? Now = null);
    /// <summary>Starts a watch on a folder: (folder, onEvent(name, isChange), onError) -> the handle (dispose = stop).</summary>
    public delegate IDisposable Watcher(string folder, Action<string?, bool> onEvent, Action<Exception> onError);

    static Host? _configured;
    public static void Configure(Host h) => _configured = h;
    readonly Host? _given;
    public DownloadsFeature() { }
    public DownloadsFeature(Host h) { _given = h; }

    sealed record ReadResult(bool Ok, string State, string? Reason, string? Folder, int Total, IReadOnlyList<ShelfItem> Items,
        int NewCount, int Partial, string? Detail, string? Code, long At);

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    bool _dry, _stopped, _test;
    string? _override;
    int _retryMs;
    IDisposable? _watcher, _debounce, _retry;
    string? _watched, _watchWhy;
    long _watchCalmUntil, _firstEventAt;
    Task<ReadResult?>? _reading;
    bool _again;
    ReadResult? _last;
    int _rev;
    string _lastSig = "", _lastPushed = "", _lastLogged = "";
    string? _seenNote;
    long _seenAt;
    Dictionary<string, string> _cur = new(), _prev = new();
    int _seq;

    long Now() => _h.Now?.Invoke() ?? _clock.Time.GetUtcNow().ToUnixTimeMilliseconds();

    public async ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        _test = ctx.Flags.Test;
        _dry = _test;
        _retryMs = _h.RetryMs ?? RetryMsDefault;
        var env = Environment.GetEnvironmentVariable(DirEnv);
        if (!string.IsNullOrEmpty(env)) {
            if (_test) _override = env;
            else ctx.Log($"{DirEnv} is ignored outside --test");
        }

        // ---- seen (BAR-871/872)
        _seenAt = ctx.Settings.TryGet("seenAt", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var d) && double.IsFinite(d) ? (long)Math.Min(d, long.MaxValue / 2.0) : 0;
        if (_seenAt != 0 && !(_seenAt > 0 && _seenAt <= Now() + 86400000)) {
            ctx.Log("the saved seenAt was not a usable time (in the future, negative or not a number); reset to now");
            _seenAt = 0;
        }
        if (_seenAt == 0) { _seenAt = Now(); await SaveSeen().ConfigureAwait(true); }

        ctx.Replay(StateEvent, () => El(StateOf()));
        ctx.Command("list", c => Guard("list", async () => {
            if (!(Bool(c.Args, "cached") && _last != null)) await Read("panel").ConfigureAwait(true);
            return CommandResult.Success(El(View()));
        }));
        ctx.Command("seen", _ => Guard("seen", async () => {
            long newest = _last is { Items.Count: > 0 } ? _last.Items[0].At : 0;
            _seenAt = Math.Max(Now(), newest);
            await SaveSeen().ConfigureAwait(true);
            if (_last != null) _last = _last with { NewCount = 0, Items = _last.Items.Select(i => i with { Fresh = false }).ToList() };
            Push();
            return CommandResult.Success(El(new JsonObject { ["ok"] = true, ["seenAt"] = _seenAt, ["note"] = _seenNote }));
        }));
        ctx.Command("open", c => Guard("open", () => Act(c.Args, "open")));
        ctx.Command("reveal", c => Guard("reveal", () => Act(c.Args, "reveal")));
        ctx.Command("drag", c => Guard("drag", () => Act(c.Args, "drag")));
        ctx.Command("folder", _ => Guard("folder", FolderAsync));
        ctx.OnEnabled(on => {
            if (!on) { CloseWatch(); _retry?.Dispose(); _retry = null; _ctx.Health(true); Push(); }
            else _ = Read("turned on");
        });
        if (!ctx.Enabled) Push();
        else _ = Read("start");
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        CloseWatch();
        _retry?.Dispose(); _retry = null;
        try {
            _ctx.Broadcast(StateEvent, El(new JsonObject {
                ["on"] = _ctx.Enabled, ["ok"] = true, ["state"] = stop.Rest ? "resting" : "stopped",
                ["reason"] = stop.Rest ? $"the Downloads shelf is {(string.IsNullOrEmpty(stop.Reason) ? "resting" : stop.Reason)}; it is back when the game exits"
                    : $"the Downloads shelf is stopped ({(string.IsNullOrEmpty(stop.Reason) ? "Mantle put Windows back" : stop.Reason)})",
                ["count"] = 0, ["newCount"] = 0, ["partial"] = 0, ["watching"] = false, ["rev"] = _rev + 1,
            }));
        } catch (Exception) { /* no pages left to tell */ }
        return ValueTask.CompletedTask;
    }

    async Task SaveSeen()
    {
        StoreWriteish w;
        try { var r = await _ctx.Settings.SetAsync("seenAt", JsonValue.Create(_seenAt)).ConfigureAwait(true); w = new(r.Ok, r.Reason); }
        catch (Exception ex) { w = new(false, ex.GetType().Name); }
        if (w.Ok) _seenNote = null;
        else {
            _seenNote = "Mantle could not save when you last looked here, so the new-download dot may come back after a restart.";
            _ctx.Log($"could not save seenAt ({Clean(w.Reason ?? "write failed")})");
        }
    }
    readonly record struct StoreWriteish(bool Ok, string? Reason);

    // ------------------------------------------------------------------ reasons without names

    string Clean(string? text, string? abs = null)
    {
        var t = text ?? "";
        var cuts = new List<(string, string)>();
        if (abs != null) { cuts.Add((abs, "it")); cuts.Add((Path.GetFileName(abs), "it")); }
        if (_last?.Folder is { } f) cuts.Add((f, "the Downloads folder"));
        if (_override != null) cuts.Add((_override, "the Downloads folder"));
        foreach (var (s, w) in cuts) if (s.Length > 1) t = Regex.Replace(t, Regex.Escape(s), w, RegexOptions.IgnoreCase);
        return t;
    }

    // ------------------------------------------------------------------ the read

    /// The folder this run reads: the test folder, else Windows' known Downloads folder; null when unknown.
    string? FolderOf(out string? why)
    {
        why = null;
        if (_override != null) return _override;
        if (_h.Folder != null) return _h.Folder();
        if (_test) { why = NoTestDir; return null; }
        return KnownDownloads();
    }

    async Task<ReadResult> ReadOnce()
    {
        long now = Now();
        string? folder = FolderOf(out var why);
        if (folder == null && why == NoTestDir)
            return new(true, "ok", null, null, 0, Array.Empty<ShelfItem>(), 0, 0, Sentenceish(why), null, now);
        if (folder == null) return new(false, "unresolved", "Windows did not say where your Downloads folder is", null, 0, Array.Empty<ShelfItem>(), 0, 0, null, null, now);
        // off the host thread: a folder on a share that went away can block a read for many seconds
        var r = await Task.Run(() => DownloadsList.ListFolder(folder, FetchMax, attributes: true)).ConfigureAwait(true);
        if (!r.Ok) return new(false, r.Why?.State ?? "error", Clean(r.Why?.Reason ?? "the Downloads folder could not be read and no reason was given"), folder, 0,
            Array.Empty<ShelfItem>(), 0, 0, null, r.Why?.Code, now);
        long seen = _seenAt;
        var sh = DownloadsList.MakeShelf(r.Items, seen, now, ShelfMax);
        string? detail = r.Unspellable switch {
            0 => null,
            1 => "1 item is not shown: its name has a broken character, so Mantle cannot open it; open the Downloads folder to see it.",
            int n => $"{n} items are not shown: their names have a broken character, so Mantle cannot open them; open the Downloads folder to see them.",
        };
        return new(true, "ok", null, folder, Math.Max(0, r.Total - sh.Partial - sh.Skipped), sh.Items, sh.NewCount, sh.Partial, detail, null, now);
    }
    static string Sentenceish(string s) => DownloadsList.Sentence(s);

    /// One read at a time; an ask during a read runs one more read after it. Never throws.
    Task<ReadResult?> Read(string why)
    {
        if (_stopped) return Task.FromResult(_last);
        if (_reading != null) { _again = true; return _reading; }
        _reading = Loop();
        return _reading;

        async Task<ReadResult?> Loop()
        {
            do {
                _again = false;
                ReadResult r;
                try { r = await ReadOnce().ConfigureAwait(true); }
                catch (Exception ex) { r = new(false, "error", "reading the Downloads folder failed inside Mantle: " + Clean(ex.Message), null, 0, Array.Empty<ShelfItem>(), 0, 0, null, null, Now()); }
                if (_stopped) break;
                Took(r, why);
            } while (_again && !_stopped);
            _reading = null;
            return _last;
        }
    }

    void Took(ReadResult r, string why)
    {
        _last = r;
        var sig = string.Join("|", r.State, r.Reason, r.Folder, r.NewCount, r.Partial, string.Join(",", r.Items.Select(i => $"{i.Name}:{i.Size}:{i.At}")));
        if (sig != _lastSig) { _lastSig = sig; _rev++; }
        if (r.Ok) { if (r.Folder != null) EnsureWatch(r.Folder); _ctx.Health(true); }
        else { CloseWatch(); _watchWhy = r.Reason; SchedRetry(); _ctx.Health(false, r.Reason); }
        string line = r.Ok ? $"{r.Items.Count} items, {r.NewCount} new{(r.Partial > 0 ? $", {r.Partial} downloading" : "")}"
            : $"{r.State}: {r.Reason}{(r.Code != null ? $" ({r.Code})" : "")}";
        if (line != _lastLogged) { _lastLogged = line; _ctx.Log($"{line} [{why}]"); }
        Push();
    }

    // ------------------------------------------------------------------ the watch (BAR-898/899)

    void EnsureWatch(string dir)
    {
        if (_stopped || !_ctx.Enabled) return;
        if (_watcher != null && _watched == dir) return;
        // a watch that just failed is not re-armed by the read its failure asked for (read -> watch -> error -> read
        // would spin as fast as the disk answers); the retry timer re-arms it
        if (_watcher == null && Now() < _watchCalmUntil) { SchedRetry(); return; }
        CloseWatch();
        try {
            var watch = _h.Watch ?? DefaultWatch;
            _watcher = watch(dir, (name, change) => _clock.Post(() => OnEvent(name, change)), ex => _clock.Post(() => OnWatchError(ex)));
            _watched = dir;
            _watchWhy = null;
            _retry?.Dispose(); _retry = null;
        } catch (Exception ex) {
            _watcher = null; _watched = null;
            _watchWhy = WatchWords;
            _ctx.Log($"the folder cannot be watched ({ex.GetType().Name})");
            SchedRetry();
        }
    }

    void OnWatchError(Exception ex)
    {
        if (_stopped || _watcher == null) return;
        _ctx.Log($"the folder watch stopped ({ex.GetType().Name})");
        CloseWatch();
        _watchWhy = WatchWords;
        _watchCalmUntil = Now() + _retryMs;
        SchedRetry();
        Push();
        // the usual cause is the folder itself going away: read now, so the button and the panel say so at once
        _ = Read("watch stopped");
    }

    void CloseWatch()
    {
        try { _watcher?.Dispose(); } catch (Exception) { /* already closed */ }
        _watcher = null; _watched = null;
        _debounce?.Dispose(); _debounce = null;
    }

    void SchedRetry()
    {
        if (_retry != null || _stopped) return;
        _retry = _clock.Later(TimeSpan.FromMilliseconds(_retryMs), () => { _retry = null; if (!_stopped && _ctx.Enabled) _ = Read("retry"); });
    }

    void OnEvent(string? name, bool change)
    {
        if (_stopped || _watcher == null) return;
        // a browser writing its partial file cannot change the shelf; its creation and rename to the real name do
        if (change && name != null && DownloadsList.SkipReason(Path.GetFileName(name)) == "partial") return;
        long t = Now();
        if (_debounce != null) _debounce.Dispose(); else _firstEventAt = t;
        long wait = Math.Max(0, Math.Min(DebounceMs, _firstEventAt + MaxWaitMs - t));
        _debounce = _clock.Later(TimeSpan.FromMilliseconds(wait), () => { _debounce = null; _ = Read("watch"); });
    }

    static IDisposable DefaultWatch(string dir, Action<string?, bool> onEvent, Action<Exception> onError)
    {
        var w = new FileSystemWatcher(dir) {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        w.Created += (_, e) => onEvent(e.Name, false);
        w.Deleted += (_, e) => onEvent(e.Name, false);
        w.Renamed += (_, e) => onEvent(e.Name, false);
        w.Changed += (_, e) => onEvent(e.Name, true);
        w.Error += (_, e) => onError(e.GetException());
        w.EnableRaisingEvents = true;
        return w;
    }

    // ------------------------------------------------------------------ state push

    JsonObject StateOf()
    {
        if (!_ctx.Enabled) return new() { ["on"] = false, ["ok"] = true, ["state"] = "off", ["reason"] = Off, ["count"] = 0, ["newCount"] = 0, ["partial"] = 0, ["watching"] = false, ["rev"] = _rev };
        if (_last == null) return new() { ["on"] = true, ["ok"] = true, ["state"] = "reading", ["reason"] = null, ["count"] = 0, ["newCount"] = 0, ["partial"] = 0, ["watching"] = _watcher != null, ["rev"] = _rev };
        return new() {
            ["on"] = true, ["ok"] = _last.Ok, ["state"] = _last.State, ["reason"] = _last.Reason,
            ["count"] = _last.Items.Count, ["newCount"] = _last.NewCount, ["partial"] = _last.Partial,
            ["watching"] = _watcher != null, ["watchReason"] = _watcher != null ? null : _watchWhy, ["rev"] = _rev,
        };
    }

    void Push()
    {
        if (_stopped) return;
        var st = StateOf();
        var sig = st.ToJsonString();
        if (sig == _lastPushed) return;
        _lastPushed = sig;
        try { _ctx.Broadcast(StateEvent, El(st)); } catch (Exception ex) { _ctx.Log($"the state push failed: {ex.GetType().Name}"); }
    }

    // ------------------------------------------------------------------ the panel's view

    string TokenOf(string abs)
    {
        foreach (var (t, p) in _cur) if (p == abs) return t;
        foreach (var (t, p) in _prev) if (p == abs) return t;
        return "d" + (++_seq);
    }

    JsonObject View()
    {
        var r = _last;
        if (r == null) return new() { ["ok"] = false, ["state"] = "reading", ["reason"] = "the Downloads folder has not been read yet", ["items"] = new JsonArray() };
        var next = new Dictionary<string, string>();
        var items = new JsonArray();
        foreach (var i in r.Items) {
            if (r.Folder == null) break;
            var abs = Path.Combine(r.Folder, i.Name);
            var tok = TokenOf(abs);
            next[tok] = abs;
            items.Add((JsonNode)new JsonObject { ["item"] = tok, ["name"] = i.Name, ["dir"] = i.Dir, ["size"] = i.Size, ["at"] = i.At, ["fresh"] = i.Fresh });
        }
        _prev = _cur; _cur = next;
        return new() {
            ["ok"] = r.Ok, ["state"] = r.State, ["reason"] = r.Reason, ["folder"] = r.Folder, ["items"] = items, ["total"] = r.Total,
            ["newCount"] = r.NewCount, ["partial"] = r.Partial, ["detail"] = r.Detail, ["watching"] = _watcher != null,
            ["watchReason"] = _watcher != null || r.Folder == null ? null : _watchWhy, ["seenNote"] = _seenNote, ["rev"] = _rev, ["at"] = r.At, ["test"] = _test,
            ["retryMs"] = r.Ok ? null : _retryMs,
        };
    }

    string? PathOf(JsonElement args)
    {
        var k = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("item", out var it) && it.ValueKind == JsonValueKind.String ? it.GetString() ?? "" : "";
        return _cur.TryGetValue(k, out var p) ? p : _prev.TryGetValue(k, out p) ? p : null;
    }

    static Task<bool> Exists(string abs) => Task.Run(() => File.Exists(abs) || Directory.Exists(abs));

    // ------------------------------------------------------------------ commands

    async ValueTask<CommandResult> Guard(string verb, Func<Task<CommandResult>> fn)
    {
        if (!_ctx.Enabled) return CommandResult.Fail(Off);
        try { return await fn().ConfigureAwait(true); }
        catch (Exception ex) {
            _ctx.Log($"{verb} threw ({ex.GetType().Name})");
            return CommandResult.Fail($"{verb} failed inside Mantle: {Clean(ex.Message)}");
        }
    }

    /// Windows' own open errors, in words that say what to do next.
    public static string OpenWhy(string t)
    {
        if (Regex.IsMatch(t, "no application is associated", RegexOptions.IgnoreCase)) return "Windows has no app set for this kind of file. Use Show in folder, then right-click it and choose Open with";
        if (Regex.IsMatch(t, "access is denied|EACCES|EPERM", RegexOptions.IgnoreCase)) return "Windows did not let Mantle open it (access denied)";
        if (Regex.IsMatch(t, "cannot find the (file|path)|ENOENT", RegexOptions.IgnoreCase)) return "it is no longer in Downloads (moved or deleted)";
        return Regex.Replace(t, @"\s*\((?:0x[0-9a-f]+|E[A-Z]{3,}|HRESULT[^)]*)\)", "", RegexOptions.IgnoreCase);
    }

    async Task<CommandResult> Act(JsonElement args, string verb)
    {
        var abs = PathOf(args);
        if (abs == null) { _ = Read("stale row"); return CommandResult.Fail(Gone); }
        if (!await Exists(abs).ConfigureAwait(true)) { _ = Read("gone"); return CommandResult.Fail(NoLonger); }
        if (verb == "drag") return CommandResult.Success(El(new JsonObject { ["ok"] = true, ["path"] = abs }));   // the bar runs the drag itself; results are never logged
        if (_dry) {
            _ctx.Log($"dry run (--test): would {(verb == "open" ? "open a row with its default app" : "show a row selected in Explorer")}; nothing was opened");
            return CommandResult.Success(El(new JsonObject { ["ok"] = true, ["dryRun"] = true,
                ["reason"] = verb == "open" ? "test run: would open it with its default app" : "test run: would show it selected in Explorer" }));
        }
        var err = verb == "open" ? (_h.Open ?? DefaultOpen)(abs) : (_h.Reveal ?? DefaultReveal)(abs);
        if (err != null) return CommandResult.Fail(verb == "open" ? OpenWhy(Regex.Replace(Clean(err, abs), @"^could not open it:\s*", "", RegexOptions.IgnoreCase)) : Clean(err, abs));
        return CommandResult.Success(El(new JsonObject { ["ok"] = true }));
    }

    async Task<CommandResult> FolderAsync()
    {
        var f = _last?.Folder ?? FolderOf(out _);
        if (f == null) return CommandResult.Fail("the Downloads folder is not known yet: " + (_last?.Reason ?? (_test && _override == null ? NoTestDir : "it has not been read")));
        if (!await Task.Run(() => Directory.Exists(f)).ConfigureAwait(true)) return CommandResult.Fail("the Downloads folder does not exist (it was moved, renamed or deleted)");
        if (_dry) {
            _ctx.Log("dry run (--test): would open the Downloads folder; nothing was opened");
            return CommandResult.Success(El(new JsonObject { ["ok"] = true, ["dryRun"] = true, ["reason"] = "test run: would open the Downloads folder" }));
        }
        var err = (_h.Open ?? DefaultOpen)(f);
        return err != null ? CommandResult.Fail(Clean(err)) : CommandResult.Success(El(new JsonObject { ["ok"] = true }));
    }

    static string? DefaultOpen(string abs)
    {
        try { using var p = Process.Start(new ProcessStartInfo(abs) { UseShellExecute = true }); return null; }
        catch (Win32Exception ex) { return ex.Message; }
        catch (Exception ex) { return ex.Message; }
    }

    static string? DefaultReveal(string abs)
    {
        try {
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{abs}\"") { UseShellExecute = false };
            using var p = Process.Start(psi);
            return null;
        } catch (Exception ex) { return ex.Message; }
    }

    // ------------------------------------------------------------------ Windows' known Downloads folder

    static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid rfid, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")] static extern void CoTaskMemFree(IntPtr p);

    /// FOLDERID_Downloads (follows a Downloads folder moved into OneDrive or another drive); null when Windows does not say.
    static string? KnownDownloads()
    {
        try {
            var id = FolderIdDownloads;
            int hr = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p);
            try { return hr == 0 && p != IntPtr.Zero ? Marshal.PtrToStringUni(p) : null; }
            finally { if (p != IntPtr.Zero) CoTaskMemFree(p); }
        } catch (Exception) { return null; }
    }

    static bool Bool(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
}
