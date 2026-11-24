using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Clipboard;

/// <summary>
/// CLIPBOARD HISTORY (lib/features/clipboard.js), lane r9-clipemoji-2: the service half. The history itself (capture,
/// privacy flags, dedupe, limits, the DPAPI pins file, paste) is sidecar/ClipHistory.cs, linked unchanged (PLAN A4); this
/// feature starts it (clip.start with <see cref="ClipHost.PinsFor"/>'s choice), keeps its health, takes Win+V, and talks
/// to the clipboard panel surface through events (<see cref="ShowEvent"/>, <see cref="HideEvent"/>, <see cref="ChangedEvent"/>,
/// <see cref="WarmEvent"/>, <see cref="NoticeEvent"/>, <see cref="StateEvent"/>) and commands
/// (<c>clipboard.ready/paste/pin/delete/clear/close/list</c>).
///
/// Win+V is a toggle (lib/surface-gate.js): from closed it opens (clip.arm first, then the list, cached when nothing
/// changed); a press while open OR still opening closes it, so a second press during the arm wait cancels the first and
/// nothing is shown; Esc (<c>clipboard.close</c>) while opening means it never appears.
///
/// DEVIATION (until the panel surface exists): the feature is not healthy until the panel says it is up
/// (<c>clipboard.ready</c>), so with no panel the host hands Win+V back to Windows' own clipboard history, and the sidecar
/// does not record copies at all (clip.start waits for the panel: no panel, nothing of the user's is held). Electron
/// starts the history as soon as the sidecar is ready, because its feature makes its own window.
///
/// PRIVACY: no log line, health text or event this feature writes carries copied content; the list goes only to the panel.
/// --test: the sidecar does not listen (StartRequest), the cache is never used, a paste is a dry run.
/// </summary>
[Feature("clipboard", "Clipboard history (Win+V)", true, Chords = new[] { "Win+V" }, Rest = true)]
public sealed class ClipboardFeature : IFeature
{
    /// {list, error?, arm}: open the panel with this list where arm says (ClipModel.Place).
    public const string ShowEvent = "clipboard.show";
    /// {why}: hide the panel ("toggle", "esc", "paste", "off", "notice", "rest", "stop").
    public const string HideEvent = "clipboard.hide";
    /// {list}: a copy came while the panel is open; redraw in place (ClipModel.Reselect).
    public const string ChangedEvent = "clipboard.changed";
    /// {list}: the start-up warm-up; draw it in the hidden window, show nothing (CLIP-007).
    public const string WarmEvent = "clipboard.warm";
    /// {title, text, ms}: a notice card, shown inactive (no focus) when the panel is closed.
    public const string NoticeEvent = "clipboard.notice";
    /// {panel, ok, why?}: given to each new subscriber; a panel that sees panel:false calls clipboard.ready (a new install
    /// after a rest or a restart knows nothing of the panel made before it).
    public const string StateEvent = "clipboard.state";

    public const string NoPanel = "the clipboard panel has not started, so Win+V stays Windows' own clipboard history";
    public const string BusyLine = "a copy was missed: another app held the clipboard";
    public const string ArmWords = "Mantle could not tell which window you came from ({0}), so choosing an item will copy it but not paste it.";
    public const int NoticeMs = 6000;
    public static readonly TimeSpan StartTimeout = TimeSpan.FromMilliseconds(5000), ArmTimeout = TimeSpan.FromMilliseconds(800),
        ListTimeout = TimeSpan.FromMilliseconds(2500), ActTimeout = TimeSpan.FromMilliseconds(2500),
        PasteTimeout = TimeSpan.FromMilliseconds(4000), StopTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>What the feature is given: where the pins live (the App passes ClipHost.PinsFor(profile, gate)), a clock
    /// (tests: the manual one), and whether the start-up warm-up runs.</summary>
    public sealed record Host(Func<ClipPinsChoice>? Pins = null, HostClock? Clock = null, bool Warm = true);

    static Host? _configured;
    public static void Configure(Host h) => _configured = h;
    readonly Host? _given;
    public ClipboardFeature() { }
    public ClipboardFeature(Host h) { _given = h; }

    sealed record ListBox(JsonElement List);

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    readonly ClipListCache<ListBox> _cache = new();
    ClipPinsChoice _pins;
    bool _test, _stopped, _started, _starting, _panelUp, _open, _opening, _warmDone;
    string? _pageWhy = NoPanel, _sidecarWhy, _armHwnd;
    int _seq, _opens;
    IDisposable? _relist, _warm, _noticeTimer;

    /// Opens cancelled by a second Win+V or an Esc before they showed (the self-test's counter).
    public int Cancelled { get; private set; }

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
    static JsonNode? Node(JsonElement e) => e.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(e.GetRawText());
    static string? Str(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static bool Has(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out _);
    static int Int(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : 0;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        _test = ctx.Flags.Test;
        _pins = _h.Pins?.Invoke() ?? new ClipPinsChoice(null, "pins are kept for this run only: Mantle was not told where its profile folder is");
        ctx.Log(_pins.Why);

        ctx.Replay(StateEvent, () => El(StateOf()));
        ctx.Command("ready", c => {
            bool ok = !Has(c.Args, "ok") || Bool(c.Args, "ok");
            _panelUp = ok;
            _pageWhy = ok ? null : Str(c.Args, "why") ?? "the clipboard panel failed to load";
            Judge();
            if (ok) { if (_started) ScheduleWarm(); else _ = StartAsync(); }
            return new ValueTask<CommandResult>(CommandResult.Success(El(StateOf())));
        });
        ctx.Command("paste", c => Run(() => PasteAsync(ClipHost.ItemArg(Str(c.Args, "item")))));
        ctx.Command("pin", c => Run(() => ThenListAsync("clip.pin", new JsonObject { ["item"] = ClipHost.ItemArg(Str(c.Args, "item")), ["pinned"] = Bool(c.Args, "pinned") })));
        ctx.Command("delete", c => Run(() => ThenListAsync("clip.delete", new JsonObject { ["item"] = ClipHost.ItemArg(Str(c.Args, "item")) })));
        ctx.Command("clear", _ => Run(() => ThenListAsync("clip.clear", new JsonObject())));
        ctx.Command("close", _ => { Hide("esc"); return new ValueTask<CommandResult>(CommandResult.Success(El(new JsonObject()))); });
        ctx.Command("list", _ => Run(async () => {
            var (list, error) = await ListAsync().ConfigureAwait(true);
            return list is { } l ? CommandResult.Success(l) : CommandResult.Fail(error!);
        }));

        ctx.Platform.OnReady(() => { _started = false; _ = StartAsync(); });
        ctx.Platform.On("clip", e => {
            string? what = Str(e.Body, "what");
            if (what == "changed") {
                _cache.Invalidate();
                if (_open) _ = RefreshAsync(); else RelistSoon();
            } else if (what == "skipped" && Str(e.Body, "reason") == "busy") ctx.Log(BusyLine);
        });
        ctx.OnEnabled(on => {
            if (on) { _ = StartAsync(); return; }
            Hide("off");
            _cache.Invalidate();
            _started = false;
            _ = _ctx.Platform.CallAsync("clip.stop", new JsonObject(), StopTimeout);
        });
        ctx.OnChord("Win+V", c => _ = ToggleAsync(c));
        Judge();
        _ctx.Broadcast(StateEvent, El(StateOf()));
        return ValueTask.CompletedTask;
    }

    JsonObject StateOf()
    {
        var (ok, why) = ClipHost.Health(_pageWhy, _sidecarWhy);
        var o = new JsonObject { ["panel"] = _panelUp, ["ok"] = ok, ["on"] = _ctx.Enabled, ["test"] = _test };
        if (why != null) o["why"] = why;
        return o;
    }

    /// One place decides health, from the two things that can be wrong (CLIP-019): the panel's trouble, then the sidecar's.
    void Judge()
    {
        if (_stopped) return;
        var (ok, why) = ClipHost.Health(_pageWhy, _sidecarWhy);
        _ctx.Health(ok, why);
    }

    void Send(string ev, JsonObject payload)
    {
        try { _ctx.Broadcast(ev, El(payload)); }
        catch (Exception ex) { _ctx.Log($"{ev} did not go out ({ex.GetType().Name})"); }
    }

    async ValueTask<CommandResult> Run(Func<Task<CommandResult>> fn)
    {
        if (_stopped || !_ctx.Enabled) return CommandResult.Fail("clipboard history is switched off in the Mantle tray menu");
        try { return await fn().ConfigureAwait(true); }
        catch (ArgumentException ex) { return CommandResult.Fail(ex.Message); }
        catch (Exception ex) {
            _ctx.Log($"a panel command threw ({ex.GetType().Name})");
            return CommandResult.Fail("that did not work inside Mantle (" + ex.GetType().Name + ")");
        }
    }

    // ---- the sidecar side --------------------------------------------------------------------------------------------

    async Task StartAsync()
    {
        _cache.Invalidate();   // a new sidecar or the pins file: read it again
        if (_stopped || !_ctx.Enabled || !_panelUp || _starting) return;
        _starting = true;
        PlatformReply r;
        try { r = await _ctx.Platform.CallAsync("clip.start", ClipHost.StartRequest(_pins, _test), StartTimeout).ConfigureAwait(true); }
        finally { _starting = false; }
        if (_stopped) return;
        string? mode = r.Ok ? Str(r.Result, "mode") : null;
        _sidecarWhy = ClipHost.StartWhy(r.Ok, r.Error, _test, mode, r.Ok ? Str(r.Result, "error") : null);
        if (_sidecarWhy == null) {
            _started = true;
            string? pinsError = Str(r.Result, "pinsError");
            _ctx.Log($"started: mode {mode}, {Int(r.Result, "count")} items ({Int(r.Result, "pinned")} pinned)" + (pinsError != null ? ", pins: " + pinsError : ""));
            ScheduleWarm();
        }
        Judge();
        Send(StateEvent, StateOf());
    }

    async Task<(JsonElement? List, string? Error)> ListAsync()
    {
        int v = _cache.BeginRead();
        PlatformReply r = await _ctx.Platform.CallAsync("clip.list", new JsonObject(), ListTimeout).ConfigureAwait(true);
        if (!r.Ok) return (null, "the clipboard history could not be read: " + r.Error);
        _cache.EndRead(v, new ListBox(r.Result));
        return (r.Result, null);
    }

    /// After a copy while the panel is closed: one read once copies stop (CLIP-005), so the next Win+V has it.
    void RelistSoon()
    {
        if (_stopped || _test) return;
        _relist?.Dispose();
        _relist = _clock.Later(TimeSpan.FromMilliseconds(ClipHost.RelistMs), () => {
            _relist = null;
            if (!_stopped && !_open) _ = ListAsync();
        });
    }

    async Task RefreshAsync()
    {
        var (list, error) = await ListAsync().ConfigureAwait(true);
        if (_stopped || !_open) return;
        if (list is { } l) Send(ChangedEvent, new JsonObject { ["list"] = Node(l) });
        else _ctx.Log(error!);
    }

    /// The first open after start is warm (CLIP-007): once, WarmMs after the history and the panel are both up, arm
    /// (read only) and the list, drawn in the hidden panel. Skipped when an open came first.
    void ScheduleWarm()
    {
        if (_warm != null || _warmDone || _opens > 0 || !_started || !_panelUp || !_h.Warm
            || Environment.GetEnvironmentVariable("MANTLE_CLIP_WARMUP") == "0") return;
        _warm = _clock.Later(TimeSpan.FromMilliseconds(ClipHost.WarmMs), () => _ = WarmAsync());
    }

    async Task WarmAsync()
    {
        _warmDone = true;
        if (_stopped || _open || _opening || _opens > 0) return;
        long t0 = _clock.NowMs;
        PlatformReply a = await _ctx.Platform.CallAsync("clip.arm", new JsonObject(), ArmTimeout).ConfigureAwait(true);
        var (list, error) = await ListAsync().ConfigureAwait(true);
        if (_stopped || _open || _opening || _opens > 0) { _ctx.Log("warm-up: an open came first"); return; }
        if (list is { } l) Send(WarmEvent, new JsonObject { ["list"] = Node(l) });
        _ctx.Log($"warm-up: clip.arm {(a.Ok ? "ok" : "failed")}, clip.list {(list != null ? "read" : error)}, {_clock.NowMs - t0} ms");
    }

    // ---- open / hide -------------------------------------------------------------------------------------------------

    /// Win+V: open, or close when open or still opening (the last press wins, nothing flashes).
    async Task ToggleAsync(string from)
    {
        if (_stopped || !_ctx.Enabled) return;
        if (_open || _opening) { Hide(_opening ? "pressed again while opening" : "toggle"); return; }
        int seq = ++_seq;
        _opening = true;
        _noticeTimer?.Dispose(); _noticeTimer = null;
        long t0 = _clock.NowMs;
        try {
            // FIRST, before anything of Mantle's can take the foreground: what Win+V was pressed over. The list goes out
            // right behind it, or not at all when the cached one is current (never in a test).
            ValueTask<PlatformReply> armCall = _ctx.Platform.CallAsync("clip.arm", new JsonObject(), ArmTimeout);
            ListBox? cached = _cache.Current(_test);
            Task<(JsonElement? List, string? Error)> listCall = cached != null ? Task.FromResult<(JsonElement?, string?)>((cached.List, null)) : ListAsync();
            PlatformReply a = await armCall.ConfigureAwait(true);
            var (list, error) = await listCall.ConfigureAwait(true);
            if (seq != _seq || _stopped || !_ctx.Enabled) { Cancelled++; return; }

            JsonNode arm;
            if (a.Ok) { arm = Node(a.Result) ?? new JsonObject(); _armHwnd = Str(a.Result, "hwnd"); }
            else { arm = new JsonObject { ["error"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, ArmWords, a.Error) }; _armHwnd = null; }
            var payload = new JsonObject { ["list"] = list is { } l ? Node(l) : null, ["arm"] = arm, ["from"] = from };
            if (list == null) payload["error"] = error;
            _opening = false;
            _open = true;
            _opens++;
            _warm?.Dispose(); _warm = null;
            Send(ShowEvent, payload);
            string at = a.Ok && Has(a.Result, "caret") && a.Result.GetProperty("caret").ValueKind == JsonValueKind.Object ? "caret" : "pointer";
            _ctx.Log($"opened by {from} in {_clock.NowMs - t0} ms ({(cached != null ? "list cached" : "list read")}): " +
                     $"{(list is { } c ? Int(c, "count").ToString(System.Globalization.CultureInfo.InvariantCulture) : "no")} items, at the {at}");
        } finally {
            if (seq == _seq) _opening = false;
        }
    }

    /// Esc, click-away, a paste, the switch: closes an open panel and cancels one still on its way.
    void Hide(string why)
    {
        _seq++;
        _opening = false;
        if (!_open) return;
        _open = false;
        Send(HideEvent, new JsonObject { ["why"] = why });
    }

    void Notice(string title, string? text)
    {
        Send(NoticeEvent, new JsonObject { ["title"] = title, ["text"] = text ?? "", ["ms"] = NoticeMs });
        _noticeTimer?.Dispose();
        _noticeTimer = _clock.Later(TimeSpan.FromMilliseconds(NoticeMs), () => {
            _noticeTimer = null;
            if (!_open && !_stopped) Send(HideEvent, new JsonObject { ["why"] = "notice" });
        });
    }

    // ---- the panel's commands ----------------------------------------------------------------------------------------

    async Task<CommandResult> PasteAsync(string item)
    {
        string target = _armHwnd ?? "0";
        Hide("paste");          // first: the window the user came from has to be able to come back
        bool dry = _test;       // a test instance NEVER writes the clipboard or types
        PlatformReply r = await _ctx.Platform.CallAsync("clip.paste", new JsonObject { ["item"] = item, ["hwnd"] = target, ["dryRun"] = dry }, PasteTimeout).ConfigureAwait(true);
        bool pasted = r.Ok && Bool(r.Result, "pasted");
        if (!r.Ok) {
            _ctx.Log($"paste failed: {r.Error}");
            Notice("Not pasted", r.Error);
        } else if (!dry && !pasted) {
            string? reason = Str(r.Result, "reason");
            _ctx.Log($"copied, not pasted: {reason}");
            Notice("Copied, not pasted", reason);
        } else {
            string formats = r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("formats", out var f) && f.ValueKind == JsonValueKind.Array
                ? string.Join("+", f.EnumerateArray().Select(x => Str(x, "name")).Where(n => n != null)) : "";
            _ctx.Log($"{(dry ? "dry-run " : "")}paste: {formats}");
        }
        return r.Ok ? CommandResult.Success(El(new JsonObject { ["pasted"] = pasted, ["dryRun"] = dry })) : CommandResult.Fail(r.Error!);
    }

    async Task<CommandResult> ThenListAsync(string verb, JsonObject args)
    {
        _cache.Invalidate();   // the cached list is out of date from here
        PlatformReply r = await _ctx.Platform.CallAsync(verb, args, ActTimeout).ConfigureAwait(true);
        if (!r.Ok) return CommandResult.Fail(r.Error!);
        var (list, error) = await ListAsync().ConfigureAwait(true);
        return list is { } l ? CommandResult.Success(l) : CommandResult.Fail(error!);
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        Hide(stop.Rest ? "rest" : "stop");
        _stopped = true;
        _relist?.Dispose(); _relist = null;
        _warm?.Dispose(); _warm = null;
        _noticeTimer?.Dispose(); _noticeTimer = null;
        // Resting while a game runs: only the panel goes. The history lives in the sidecar and keeps listening, so Win+V
        // after the game still has it all (CLIP-018). No clip.stop here, rest or not: the host has already marked the
        // feature stopped, so its platform refuses calls. Quitting ends the sidecar and the memory-only history with it;
        // switching the feature off is the OnEnabled path, which does send clip.stop.
        return ValueTask.CompletedTask;
    }
}
