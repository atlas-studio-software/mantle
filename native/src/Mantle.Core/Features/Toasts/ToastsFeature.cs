using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Features.Notifications;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Toasts;

// The feature half of lib/features/toasts.js: Mantle's own notification banners. WHEN a banner shows and WITH WHAT
// is decided here; the card, the stack, the timing and the place are the surface's (Mantle.App/Surfaces/Toasts), which
// hears "toasts.show" / "toasts.ids" / "toasts.update" / "toasts.drop" and asks toasts.layout / click / more / action /
// copy / ready / state.
//
// WHERE THE TOASTS COME FROM: the notification list (Features/Notifications/NotificationFeed: the sidecar's list and
// watch, read only). A toast is new when its id was not in the previous list (BannerPicker); it becomes a Mantle
// banner only for an app whose Windows banner Mantle has taken (BannerOwner.Owns): every other app keeps Windows' own.
// Windows would be quiet (Do not disturb, full screen, a game, presentation, the lock screen: toasts.quiet +
// ToastsCore.QuietWhy) = no banner, said once in the log; the toast is in the list either way.
//
// THE TAKEOVER (Windows' banner off per app, ShowBanner = 0 through the sidecar, originals in notif-banners.json first):
// ONLY in a --live run (the sole shell: the claim, Host) whose host configured the record's place (Configure, from
// App.xaml.cs). A --test or scratch run makes no owner at all, so nothing of Windows' is read for writing or written,
// and no app's banner can be Mantle's there: the UI tests drive banners through the test-only commands (testShow,
// testOwn) and Mantle's own banners. Put back: switched off, stopped (not a game rest), notification access lost; the
// helper (sidecar) down = BannerSafety's grace then tools/restore-banners.ps1; Mantle leaving with it down = the script
// to its end. The sidecar's own exit and the crash supervisor put back from the same record (part 1).
//
// MANTLE'S OWN BANNERS (OwnBanners, Shared): shown with the switch off and without notification access (they are
// Mantle's, not an app's), held while Windows would be quiet and tried again every OwnRetryMs.
//
// Not ported (said in docs/lanes/osd-toasts.md): the early "toast coming" pre-warm (the native window costs ~nothing
// to make), a click that brings the app's own window forward or opens the bell's list (no native owner yet: a click
// asks the sidecar to do what clicking it in Windows would do), the screenshot card / bar pop-up corner occupants.

/// <summary>One card as the surface draws it: an app's toast or Mantle's own banner.</summary>
public sealed record ToastCard(long Id, string? Aumid, string App, string? Icon, string? Logo, bool LogoCircle, string? Title, string? Body,
    string? Attribution, string? Image, bool Sticky, string Opens, bool Own, string? Detail, OwnProgress? Progress, string Tone,
    IReadOnlyList<OwnButton> Actions, double At)
{
    public static ToastCard Of(Banner b) => new(b.Id, b.Aumid, b.App, b.Icon, b.Logo, b.LogoCircle, b.Title, b.Body, b.Attribution, b.Image,
        b.Sticky, b.Opens, false, null, null, "info", Array.Empty<OwnButton>(), b.At);
    public static ToastCard Of(OwnBanner b, double at) => new(b.Id, null, b.App, null, null, false, b.Title, b.Body, null, null,
        true, "none", true, b.Detail, b.Progress, b.Tone, b.Actions, at);
}

/// <summary>New cards (Ids: every id still listed, or null = keep what is up).</summary>
public sealed record ToastsShow(IReadOnlyList<ToastCard> Banners, IReadOnlyList<long>? Ids);
/// <summary>Every id still in the list (and Mantle's own): a card whose id is gone leaves.</summary>
public sealed record ToastsIds(IReadOnlyList<long> Ids);
/// <summary>Mantle's own card changed in place.</summary>
public sealed record ToastsUpdate(ToastCard Banner);
/// <summary>Mantle's own card dropped by its poster.</summary>
public sealed record ToastsDrop(long Id);

[Feature("toasts", "Show notifications in Mantle's style", true, Rest = true)]
public sealed class ToastsFeature : IFeature
{
    public const int ApplyDebounceMs = 800;
    public const int OwnRetryMs = 60000;
    public const string FixUri = "ms-settings:privacy-notifications";
    public const string NoAccess = "Windows is not letting Mantle read notifications, so Windows keeps showing its own pop-ups. Allow it in Windows Settings > Privacy > Notifications.";
    public const string NotRead = "Mantle could not read your notifications just now, so Windows keeps showing its own pop-ups.";
    public const long TestIdBase = 990000;

    /// Where a --live run keeps the record and finds the put-back script (App.xaml.cs, block ToastsWiring).
    public sealed record Paths(string DataRoot, SafeFile Files, string RestoreScript);
    static Paths? _paths;
    public static void Configure(Paths p) => _paths = p;

    /// Mantle's own banners (the updater posts here); one per process.
    public static OwnBanners Shared { get; } = new();

    readonly IToastClock? _clockIn;
    readonly IBannerRegistry? _regIn;
    readonly IBannerRecordStore? _storeIn;
    readonly Func<Task<ScriptOutput>>? _runIn;
    readonly Func<ScriptOutput>? _syncIn;
    readonly OwnBanners? _ownIn;

    IToastClock _clock = null!;
    IFeatureContext _ctx = null!;
    OwnBanners _own = null!;
    BannerOwner? _owner;
    BannerSafety? _safety;
    NotificationFeed? _feed;
    readonly BannerPicker _picker = new();
    readonly List<IDisposable> _offs = new();
    readonly HashSet<long> _ownOnPage = new();
    readonly List<long> _testIds = new();
    IDisposable? _applyT, _ownRetryT;
    Task<BannerApply?>? _applyP;
    BannerApply? _lastApply;
    bool _live, _stopped, _accessOk, _helperSeen, _ownChecking, _ownAgain;
    string? _accessWhy, _access, _quiet, _quietLogged, _ownHeldWhy;
    string? _testQuiet;   // --test only: the stand-in quiet reason ("" = not quiet); null = ask toasts.quiet
    int _shownCount;
    (double H, int Count) _layout;

    public ToastsFeature() { }

    /// For the tests: a manual clock, and (for a live rig) the registry, record and script stand-ins.
    public ToastsFeature(IToastClock clock, OwnBanners? own = null, IBannerRegistry? reg = null, IBannerRecordStore? store = null,
        Func<Task<ScriptOutput>>? run = null, Func<ScriptOutput>? sync = null)
    {
        _clockIn = clock; _ownIn = own; _regIn = reg; _storeIn = store; _runIn = run; _syncIn = sync;
    }

    public BannerOwner? Owner => _owner;
    public int Shown => _shownCount;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _clock = _clockIn ?? new HostToastClock(new HostClock(null, SynchronizationContext.Current));
        _own = _ownIn ?? Shared;
        bool test = ctx.Flags.Test;
        _live = ctx.Flags.Live && !test;
        if (_live) MakeOwner();
        else ctx.Log("not a --live run, so Windows' own banners are never taken here (only test and Mantle's own banners show)");

        ctx.Command("layout", Layout);
        ctx.Command("ready", _ => new(Ok(new JsonObject { ["ok"] = true, ["test"] = test })));
        ctx.Command("click", Click);
        ctx.Command("more", _ => { _ctx.Log("\"+N more\": the bell's list has no native owner yet, so nothing opened"); return new(CommandResult.Fail("the notification list is not drawn by this build yet")); });
        ctx.Command("action", Action);
        ctx.Command("copy", Copy);
        ctx.Command("state", _ => new(Ok(StateJson())));
        if (test)
        {
            // a banner with NO Windows toast behind it (Electron's testShow): nothing is posted to, read from or removed
            // from Windows' notification centre
            ctx.Command("testShow", c => {
                int n = (int)Math.Max(1, Math.Min(4, Num(c.Args, "n") ?? 1));
                var list = new List<ToastCard>();
                for (int i = 0; i < n; i++)
                {
                    long id = TestIdBase + _testIds.Count + 1;
                    _testIds.Add(id);
                    var b = new Banner(id, "Mantle.ToastTest.corner", Str(c.Args, "app", 40) ?? "Mantle test", null, null, false,
                        Str(c.Args, "title", 200) ?? $"Corner trace {id}", Str(c.Args, "body", 600) ?? "A test banner: no Windows toast behind it.",
                        null, null, Bool(c.Args, "sticky"), "app", _clock.NowMs);
                    list.Add(ToastCard.Of(b));
                }
                Show(list);
                return new(Ok(new JsonObject { ["ok"] = true, ["ids"] = new JsonArray(list.Select(b => (JsonNode)b.Id).ToArray()) }));
            });
            // Mantle's own banner through the real OwnBanners road (post / update / drop), with buttons and detail
            ctx.Command("testOwn", c => {
                string key = Str(c.Args, "key", 40) ?? "test";
                if (Bool(c.Args, "drop")) return new(Ok(new JsonObject { ["ok"] = true, ["dropped"] = _own.Drop(key) }));
                var acts = new List<OwnButtonPost>();
                if (c.Args.ValueKind == JsonValueKind.Object && c.Args.TryGetProperty("actions", out var aa) && aa.ValueKind == JsonValueKind.Array)
                    foreach (var a in aa.EnumerateArray()) acts.Add(new OwnButtonPost(Str(a, "id", 40), Str(a, "label", 40), Bool(a, "primary")));
                // dnd / quns: a stand-in for Windows' quiet state from now on, so a test never depends on the machine's
                // (a CI runner reads as Do not disturb); without them the real toasts.quiet answers
                if (Num(c.Args, "dnd") != null || Num(c.Args, "quns") != null)
                {
                    int? dnd = Num(c.Args, "dnd") is double d1 ? (int)d1 : null, quns = Num(c.Args, "quns") is double q1 ? (int)q1 : null;
                    _testQuiet = ToastsCore.QuietWhy(true, dnd, quns) ?? "";
                    _ctx.Log($"test: Windows' quiet state stands in as dnd {dnd?.ToString() ?? "-"}, quns {quns?.ToString() ?? "-"} ({(_testQuiet.Length > 0 ? _testQuiet : "not quiet")})");
                }
                double? pct = c.Args.ValueKind == JsonValueKind.Object && c.Args.TryGetProperty("pct", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
                string? ptext = Str(c.Args, "progress", 160);
                long id = _own.Post(key, new OwnBannerPost(Str(c.Args, "title", 200), Str(c.Args, "body", 600), Str(c.Args, "detail", 4000), null,
                    Str(c.Args, "tone", 10), ptext != null ? new OwnProgressPost(ptext, pct) : null, acts),
                    async act => { _ctx.Log($"test own banner \"{key}\": \"{act}\" pressed"); await Task.Yield(); return new OwnAnswer(Keep: act != "close" && act != "dismiss"); });
                return new(Ok(new JsonObject { ["ok"] = true, ["id"] = id }));
            });
            ctx.Command("testIds", c => {
                // the list "changed": only these test ids are still there (a toast removed in the notification centre)
                var keep = new List<long>();
                if (c.Args.ValueKind == JsonValueKind.Object && c.Args.TryGetProperty("ids", out var ii) && ii.ValueKind == JsonValueKind.Array)
                    foreach (var i in ii.EnumerateArray()) if (i.TryGetInt64(out long v)) keep.Add(v);
                _testIds.RemoveAll(x => !keep.Contains(x));
                _ctx.Broadcast("toasts.ids", new ToastsIds(IdsNow()));
                return new(Ok(new JsonObject { ["ok"] = true }));
            });
        }

        // a --test run never reads the notification list: the machine's own notifications (the runner's, or the user's
        // on a laptop) would come and go under the test; its banners are the test's (testShow / testIds) and Mantle's own
        if (test) ctx.Log("test: the notification list is not read (only test banners and Mantle's own show)");
        else { _feed = new NotificationFeed(ctx.Platform, OnList, l => ctx.Log("" + l)); _feed.Start(); }
        _offs.Add(_own.OnChange(OnOwn));
        if (_own.List().Count > 0) OwnSoon("Mantle started");
        ctx.OnEnabled(on => {
            if (on) { _picker.Reset(); if (_feed?.Last is { } l) _picker.Next(l.Ok, l.Toasts, _clock.NowMs); _ = Apply("switched on", true); }
            else
            {
                DropApps("switched off");
                if (ctx.Platform.Status.State == PlatformState.Ok) _ = Restore("switched off");
                else if (_safety != null) _ = _safety.GaveUp("switched off while the notification helper was not running");
            }
            Judge();
        });
        _offs.Add(ctx.Platform.OnStatus(st => {
            if (_stopped || _safety == null || st.State == PlatformState.Ok) return;
            if ((st.Reason ?? "").Contains("gave up", StringComparison.Ordinal)) _ = _safety.GaveUp(st.Reason);
            else _safety.Down(st.Reason, starting: !_helperSeen);
            Judge();
        }));
        if (ctx.Platform.Status.State != PlatformState.Ok) _safety?.Down("it has not started yet", starting: true);
        _offs.Add(ctx.Platform.OnReady(() => {
            _helperSeen = true;
            _safety?.Up();
            if (ctx.Enabled) _ = Apply("the sidecar started");
            else if (_owner?.Counts().Owned > 0) _ = Restore("the switch is off");   // a run that died with it on
            Judge();
        }));
        Judge();
        return ValueTask.CompletedTask;
    }

    void MakeOwner()
    {
        IBannerRegistry? reg = _regIn;
        IBannerRecordStore? store = _storeIn;
        Func<Task<ScriptOutput>>? run = _runIn;
        Func<ScriptOutput>? sync = _syncIn;
        if (store == null && _paths is { } p)
        {
            var file = new BannerRecordFile(p.DataRoot, p.Files);
            store = file;
            reg ??= new PlatformBannerRegistry(_ctx.Platform, file.Path);
            var script = new RestoreBannersScript(p.RestoreScript, file.Path, test: false);
            run ??= script.RunAsync; sync ??= script.RunSync;
        }
        if (reg == null || store == null || run == null || sync == null)
        {
            _ctx.Log("this Mantle did not say where the banner record lives, so Windows' own banners are left alone");
            return;
        }
        _owner = new BannerOwner(reg, store, l => _ctx.Log("" + l), test: false, now: () => _clock.NowMs);
        _safety = new BannerSafety(() => _owner.Counts().Owned, run, sync, _clock, l => _ctx.Log("" + l), Judge);
    }

    public async ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        _applyT?.Dispose(); _applyT = null;
        _ownRetryT?.Dispose(); _ownRetryT = null;
        foreach (var o in _offs) o.Dispose();
        _offs.Clear();
        _feed?.Dispose();
        _ctx.Broadcast("toasts.ids", new ToastsIds(Array.Empty<long>()));   // every card goes
        _safety?.Dispose();
        if (stop.Rest || _owner == null) return;   // a game rest keeps Windows' banners off: the toasts wait in the list
        if (_ctx.Platform.Status.State != PlatformState.Ok)
        {
            _safety?.Leave($"{stop.Reason} while its notification helper was not running");
            return;
        }
        try { if (_applyP != null) await _applyP; } catch (Exception) { /* its own log said why */ }
        await Restore(stop.Reason, keepReleased: true);
    }

    // ------------------------------------------------------------------ take and put back

    Task<BannerApply?> Apply(string why, bool fresh = false)
    {
        if (_stopped || _owner == null || !_ctx.Enabled || !_accessOk || _accessWhy != null || _ctx.Platform.Status.State != PlatformState.Ok)
            return Task.FromResult<BannerApply?>(null);
        var owner = _owner;
        var idle = _safety?.Idle() ?? Task.CompletedTask;
        _applyP = Run();
        return _applyP;
        async Task<BannerApply?> Run()
        {
            await idle;   // a no-helper put-back still running finishes first, so a take never races it
            var r = await owner.ApplyAsync(why, fresh);
            _lastApply = r;
            Judge();
            return r;
        }
    }

    void ApplySoon(string why)
    {
        _applyT?.Dispose();
        _applyT = _clock.Later(ApplyDebounceMs, () => { _applyT = null; _ = Apply(why); });
    }

    async Task<BannerRestore?> Restore(string why, bool keepReleased = false)
    {
        if (_owner == null) return null;
        var r = await _owner.RestoreAsync(why, keepReleased);
        if (!r.Ok) _ctx.Log($"put back: {r.Reason}");
        return r;
    }

    // ------------------------------------------------------------------ the list

    void Learn(NotificationList p)
    {
        string? was = _accessWhy; bool wasOk = _accessOk;
        if (p.Access != null) _access = p.Access;
        _accessWhy = p.Access != null && p.Access != "Allowed" && p.Access != "unknown" ? NoAccess : !p.Ok && !p.Available ? NotRead : null;
        if (p.Access == "Allowed" && p.Available) _accessOk = true;
        else if (_accessWhy != null) _accessOk = false;
        if (_accessWhy != null && _accessWhy != was)
        {
            _ctx.Log($"no banners: {_accessWhy}");
            if (_owner?.Counts().Owned > 0) _ = Restore("notifications cannot be read", keepReleased: true);
        }
        else if (_accessOk && !wasOk) _ = Apply(was != null ? "notifications can be read again" : "notifications can be read");
        if (_accessWhy != was) Judge();
    }

    void OnList(NotificationList p)
    {
        if (_stopped) return;
        Learn(p);
        var fresh = _picker.Next(p.Ok, p.Toasts, _clock.NowMs);
        _ctx.Broadcast("toasts.ids", new ToastsIds(IdsNow()));
        if (fresh.Count == 0 || !_ctx.Enabled || _accessWhy != null) return;
        var mine = new List<IncomingToast>();
        bool newApp = false;
        foreach (var t in fresh)
        {
            if (_owner != null && _owner.Owns(t.Aumid)) mine.Add(t);
            else if (_owner != null && BannerOwner.LeftToWindows(t.Aumid, false) == null && !_owner.Released(t.Aumid)) newApp = true;
        }
        if (newApp) ApplySoon("an app notified for the first time");
        if (mine.Count == 0) return;
        _ = ShowIfLoud(mine);
    }

    async Task ShowIfLoud(List<IncomingToast> mine)
    {
        string? why = await QuietAsync("the banner");
        if (_stopped || !_ctx.Enabled) return;
        _quiet = why;
        if (why != null)
        {
            if (_quietLogged != why) { _quietLogged = why; _ctx.Log($"no banner while {why}; {mine.Count} new in the list"); }
            return;
        }
        _quietLogged = null;
        _shownCount += mine.Count;
        Show(mine.Select(t => ToastCard.Of(ToastsCore.BannerOf(t, _clock.NowMs))).ToList());
    }

    /// toasts.quiet {dnd, quns} -> the reason Windows would be quiet now, or null. A failed check shows the banner (said).
    async Task<string?> QuietAsync(string what)
    {
        if (_testQuiet != null) return _testQuiet.Length > 0 ? _testQuiet : null;   // a test's stand-in (testOwn dnd / quns)
        var r = await _ctx.Platform.CallAsync("toasts.quiet", new JsonObject(), TimeSpan.FromSeconds(2));
        if (!r.Ok || r.Result.ValueKind != JsonValueKind.Object)
        {
            _ctx.Log($"quiet check failed ({r.Error ?? "no answer"}); showing {what} anyway");
            return null;
        }
        int? I(string k) => r.Result.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
        bool ok = !(r.Result.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False);
        return ToastsCore.QuietWhy(ok, I("dnd"), I("quns"));
    }

    IReadOnlyList<long> IdsNow() => _picker.Ids().Concat(_testIds).Concat(_own.List().Select(b => b.Id)).ToArray();

    void Show(IReadOnlyList<ToastCard> cards)
    {
        if (cards.Count == 0) return;
        _ctx.Broadcast("toasts.show", new ToastsShow(cards, null));
        if (_ctx.Flags.Test) _ctx.Log($"show {cards.Count}: {string.Join(", ", cards.Select(c => c.Id))}");
    }

    void DropApps(string why)
    {
        _testIds.Clear();
        _ctx.Broadcast("toasts.ids", new ToastsIds(_own.List().Select(b => b.Id).ToArray()));
        _ctx.Log($"the apps' banners went ({why}); Mantle's own stay");
    }

    // ------------------------------------------------------------------ Mantle's own banners

    List<OwnBanner> OwnWaiting() => _own.List().Where(b => !_ownOnPage.Contains(b.Id)).ToList();

    void OwnSoon(string why) => _ = OwnShow(why);

    async Task OwnShow(string why)
    {
        if (_stopped) return;
        if (_ownChecking) { _ownAgain = true; return; }
        if (OwnWaiting().Count == 0) return;
        _ownChecking = true;
        string? quiet = await QuietAsync("Mantle's own banner");
        _ownChecking = false;
        if (_stopped) return;
        var fresh = OwnWaiting();
        if (fresh.Count > 0 && quiet != null)
        {
            if (_ownHeldWhy != quiet) _ctx.Log($"Mantle's own banner held while {quiet} ({why}); it is tried again every {OwnRetryMs / 1000} s");
            _ownHeldWhy = quiet;
            _ownRetryT?.Dispose();
            _ownRetryT = _clock.Later(OwnRetryMs, () => { _ownRetryT = null; OwnSoon($"tried again after {OwnRetryMs / 1000} s"); });
        }
        else if (fresh.Count > 0)
        {
            if (_ownHeldWhy != null) _ctx.Log($"Mantle's own banner shown (it was held while {_ownHeldWhy})");
            _ownHeldWhy = null;
            _ownRetryT?.Dispose(); _ownRetryT = null;
            foreach (var b in fresh) _ownOnPage.Add(b.Id);
            Show(fresh.Select(b => ToastCard.Of(b, _clock.NowMs)).ToList());
        }
        if (_ownAgain) { _ownAgain = false; if (_ownRetryT == null) OwnSoon("posted while Windows was asked"); }
    }

    void OnOwn(OwnBannerEvent ev)
    {
        if (_stopped) return;
        if (ev.Type == OwnChange.Drop)
        {
            if (_ownOnPage.Remove(ev.Id)) _ctx.Broadcast("toasts.drop", new ToastsDrop(ev.Id));
            if (OwnWaiting().Count == 0) { _ownRetryT?.Dispose(); _ownRetryT = null; _ownHeldWhy = null; }
            return;
        }
        if (_ownOnPage.Contains(ev.Id)) { _ctx.Broadcast("toasts.update", new ToastsUpdate(ToastCard.Of(ev.Banner, _clock.NowMs))); return; }
        if (_ownRetryT != null && _ownHeldWhy != null) return;   // held while Windows is quiet: the retry shows the latest
        OwnSoon("Mantle posted one");
    }

    // ------------------------------------------------------------------ what the surface asks

    ValueTask<CommandResult> Layout(CommandCall c)
    {
        _layout = (Num(c.Args, "h") ?? 0, (int)Math.Max(0, Num(c.Args, "count") ?? 0));
        if (_layout.Count == 0) _ownOnPage.Clear();   // the page is empty: Mantle's own come back on their next post
        if (_ctx.Flags.Test) _ctx.Log($"page drew {_layout.Count} card(s), {_layout.H:0} DIP");
        return new(Ok(new JsonObject { ["ok"] = true }));
    }

    async ValueTask<CommandResult> Click(CommandCall c)
    {
        long id = (long)(Num(c.Args, "id") ?? 0);
        if (id == 0) return CommandResult.Fail("that banner is not known");
        if (id < 0) return CommandResult.Fail("a click on Mantle's own banner opens nothing; its buttons do");
        if (_testIds.Contains(id)) { _ctx.Log($"test banner {id} clicked"); return Ok(new JsonObject { ["ok"] = true, ["act"] = "test" }); }
        // what clicking it in Windows would do, as far as Windows lets another app (the sidecar's open)
        var r = await _ctx.Platform.CallAsync("notifications", new JsonObject { ["op"] = "open", ["toast"] = id }, TimeSpan.FromSeconds(6));
        bool ok = r.Ok && !(r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False);
        string? why = ok ? null : r.Error ?? (r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var re) ? re.GetString() : null) ?? "no reason";
        if (!ok) _ctx.Log($"click: the notification did not open ({why})");
        return ok ? Ok(new JsonObject { ["ok"] = true, ["act"] = "open" }) : CommandResult.Fail($"it did not open: {why}");
    }

    async ValueTask<CommandResult> Action(CommandCall c)
    {
        string act = Str(c.Args, "action", 40) ?? "";
        if (act.Length == 0) return Ok(new JsonObject { ["ok"] = false, ["keep"] = true, ["reason"] = "no action was named" });
        var r = await _own.ActAsync((long)(Num(c.Args, "id") ?? 0), act);
        return Ok(new JsonObject { ["ok"] = r.Ok, ["keep"] = r.Keep, ["reason"] = r.Reason });
    }

    ValueTask<CommandResult> Copy(CommandCall c)
    {
        long id = (long)(Num(c.Args, "id") ?? 0);
        var b = _own.List().FirstOrDefault(x => x.Id == id);
        if (b == null || string.IsNullOrEmpty(b.Detail)) return new(Ok(new JsonObject { ["ok"] = false, ["reason"] = "that banner has nothing to copy any more" }));
        if (_ctx.Flags.Test) return new(Ok(new JsonObject { ["ok"] = false, ["test"] = true, ["reason"] = "a test run never writes to the clipboard" }));
        // the surface writes it (the clipboard is the UI thread's); the feature hands over the words
        return new(Ok(new JsonObject { ["ok"] = true, ["text"] = b.Detail, ["chars"] = b.Detail.Length }));
    }

    // ------------------------------------------------------------------ health and state

    public ToastsStatusLine Status() => ToastsStatus.Of(_ctx.Enabled, _accessWhy, _lastApply is { Ok: false } a ? a.Reason : null,
        _ctx.Platform.Status.State == PlatformState.Ok, _safety?.State(), _feed?.Watching, _feed?.WatchWhy, _clock.NowMs,
        t => DateTimeOffset.FromUnixTimeMilliseconds((long)t).ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));

    void Judge()
    {
        if (_ctx == null || _stopped) return;
        if (!_ctx.Enabled) { _ctx.Health(true); return; }
        if (_safety?.State().Down != null) { var st = Status(); _ctx.Health(false, $"{st.Line} ({st.Why})"); return; }
        if (_accessWhy != null) { _ctx.Health(false, _accessWhy); return; }
        if (_lastApply is { Ok: false } a) { _ctx.Health(false, $"Windows' own pop-ups could not be turned off for every app: {a.Reason}"); return; }
        _ctx.Health(true);
    }

    JsonObject StateJson()
    {
        var c = _owner?.Counts() ?? new BannerCounts(0, 0, null);
        var st = Status();
        string? reason = _accessWhy ?? (_lastApply is { Ok: false } a ? a.Reason : null) ?? c.RecordWhy;
        if (st.Mode != "mantle" && _ctx.Enabled && reason == null) reason = $"{st.Line} ({st.Why})";
        return new JsonObject
        {
            ["ok"] = true, ["on"] = _ctx.Enabled, ["live"] = _live, ["takes"] = _owner != null, ["access"] = _access, ["accessOk"] = _accessWhy == null,
            ["accessWhy"] = _accessWhy, ["fixUri"] = FixUri, ["covered"] = c.Owned, ["released"] = c.Released, ["recordWhy"] = c.RecordWhy,
            ["quiet"] = _quiet, ["reason"] = reason, ["shown"] = _shownCount, ["layout"] = new JsonObject { ["h"] = _layout.H, ["count"] = _layout.Count },
            ["status"] = new JsonObject { ["mode"] = st.Mode, ["line"] = st.Line, ["why"] = st.Why, ["tone"] = st.Tone, ["at"] = st.At },
            ["own"] = new JsonObject { ["posted"] = _own.List().Count, ["onPage"] = _ownOnPage.Count, ["heldWhy"] = _ownHeldWhy },
            ["watch"] = new JsonObject { ["watching"] = _feed?.Watching ?? false, ["why"] = _feed?.WatchWhy },
        };
    }

    static CommandResult Ok(JsonObject o) => CommandResult.Success(FeatureOptions.Parse(o.ToJsonString()));
    static double? Num(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
    static bool Bool(JsonElement a, string k) => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string? Str(JsonElement a, string k, int max) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() is { } s && s.Length > max ? s[..max] : v.GetString()) : null;
}
