using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Emoji;

/// <summary>
/// THE EMOJI PICKER (lib/features/emoji.js), lane r9-clipemoji-2: the service half. Win+. and Win+; open the picker over
/// the app the user is typing in, which KEEPS the keyboard: the panel is shown inactive and the sidecar's key watch
/// (sidecar/Emoji.cs emoji.watch, linked unchanged; a 6 s lease renewed every 2 s while open, so a crashed Mantle cannot
/// keep the keys) reports the picker's keys as <c>emoji</c> events, passed on as <see cref="KeyEvent"/>. The watch never
/// takes a key with Ctrl, Alt or Win held (Emoji.Captured), so the kill switch Ctrl+Alt+Shift+Backspace always passes.
/// A pick is typed with Unicode SendInput (emoji.type; a dry run in --test) and remembered in <see cref="EmojiPicks"/>.
/// Over an administrator window (Mantle not elevated) the press goes to Windows' own panel (emoji.windows) with a notice.
///
/// DEVIATION (until the picker surface exists): not healthy until the panel calls <c>emoji.ready {panel}</c>, so with no
/// panel the host hands Win+. and Win+; back to Windows' own emoji panel. When there is no window to type into, the
/// answer says <c>copy:true</c> and the PANEL puts the pick on the clipboard (Core has no clipboard; Electron's feature
/// wrote it itself).
/// PRIVATE: no log line carries an emoji (counts and outcomes only).
/// </summary>
[Feature("emoji", "Emoji picker (Win+.)", true, Chords = new[] { "Win+.", "Win+;" }, Rest = true)]
public sealed class EmojiFeature : IFeature
{
    /// {arm, tone, recents, app, appPicks, closeAfter, notice?}: show the panel inactive where arm says (EmojiModel.Place).
    public const string ShowEvent = "emoji.show";
    /// {why}: hide the panel.
    public const string HideEvent = "emoji.hide";
    /// {key, text, shift}: a key the watch took while the panel is open.
    public const string KeyEvent = "emoji.key";
    /// {text, ms}: a notice (over an administrator window).
    public const string NoticeEvent = "emoji.notice";
    /// {self, closeAfter}: the watch could not start, the panel takes the keyboard itself.
    public const string KeyboardEvent = "emoji.keyboard";
    /// {panel, ok, why?}: replayed; a panel that sees panel:false calls emoji.ready.
    public const string StateEvent = "emoji.state";

    public const string ReadyWords = "Win+. opens the emoji picker";
    public const string NoPanel = "the emoji picker has not started, so Win+. stays Windows' own emoji panel";
    public const string TooOld = "the sidecar exe has no emoji commands yet: run npm run build:sidecar";
    public const string NoWindow = "Mantle couldn't tell which window you were in, so a pick is copied: paste it with Ctrl+V.";
    public const int LeaseMs = 6000, RenewMs = 2000, NoticeMs = 4000;

    public static IReadOnlyList<FeatureOption> Options { get; } = new[] {
        new FeatureOption("closeAfterInsert", "Close after picking", FeatureOptionKind.Bool, false) {
            Help = "Off: Enter types the emoji and the picker stays open, as Windows' does (Shift+Enter types and closes). On: the other way round.",
        },
    };

    /// <summary>What the feature is given: a clock (tests: the manual one).</summary>
    public sealed record Host(HostClock? Clock = null);
    static Host? _configured;
    public static void Configure(Host h) => _configured = h;
    readonly Host? _given;
    public EmojiFeature() { }
    public EmojiFeature(Host h) { _given = h; }

    IFeatureContext _ctx = null!;
    HostClock _clock = null!;
    EmojiPicks _picks = null!;
    bool _test, _stopped, _open, _watch, _kbSelf;
    string? _panelWhy = NoPanel, _sidecarWhy, _panelHwnd, _hwnd, _app;
    int _seq;
    IDisposable? _renew;

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
    static string? Str(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static bool Has(JsonElement o, string k) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(k, out _);
    static JsonArray Arr(IEnumerable<string> xs) => new(xs.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _clock = (_given ?? _configured)?.Clock ?? new HostClock();
        _test = ctx.Flags.Test;
        _picks = new EmojiPicks(ctx.Settings);

        ctx.Replay(StateEvent, () => El(StateOf()));
        ctx.Command("ready", c => {
            bool ok = !Has(c.Args, "ok") || Bool(c.Args, "ok");
            _panelHwnd = Str(c.Args, "panel") ?? _panelHwnd;
            _panelWhy = ok ? null : Str(c.Args, "why") ?? "the emoji picker failed to load";
            Judge();
            return new ValueTask<CommandResult>(CommandResult.Success(El(StateOf())));
        });
        ctx.Command("close", c => { Close(Str(c.Args, "why") ?? "page"); return new ValueTask<CommandResult>(CommandResult.Success(El(new JsonObject()))); });
        ctx.Command("tone", async c => {
            int tone = c.Args.ValueKind == JsonValueKind.Object && c.Args.TryGetProperty("tone", out var t) ? EmojiModel.ClampTone(t) : 0;
            StoreWrite w = await _picks.SetToneAsync(tone).ConfigureAwait(true);
            if (!w.Ok) _ctx.Log("the skin tone could not be saved: " + w.Reason);
            return CommandResult.Success(El(new JsonObject { ["tone"] = _picks.Tone }));
        });
        ctx.Command("insert", c => InsertAsync(c.Args));

        ctx.Platform.OnReady(() => _ = ProbeAsync());
        ctx.Platform.On("emoji", e => {
            if (!_open) return;
            if (Str(e.Body, "what") == "key")
                Send(KeyEvent, new JsonObject { ["key"] = Str(e.Body, "key"), ["text"] = Str(e.Body, "text"), ["shift"] = Bool(e.Body, "shift") });
            else { _watch = false; Close(Str(e.Body, "what") == "lost" ? "lost" : Str(e.Body, "why") ?? Str(e.Body, "what") ?? "the key watch ended"); }
        });
        ctx.OnEnabled(on => { if (!on) Close("off"); });
        foreach (string chord in new[] { "Win+.", "Win+;" }) ctx.OnChord(chord, ch => _ = ToggleAsync(ch));
        Judge();
        Send(StateEvent, StateOf());
        return ValueTask.CompletedTask;
    }

    JsonObject StateOf()
    {
        string? why = _panelWhy ?? _sidecarWhy;
        var o = new JsonObject { ["panel"] = _panelWhy != NoPanel, ["ok"] = why == null };
        if (why != null) o["why"] = why;
        return o;
    }

    void Judge()
    {
        if (_stopped) return;
        string? why = _panelWhy ?? _sidecarWhy;
        _ctx.Health(why == null, why ?? ReadyWords);
    }

    void Send(string ev, JsonObject payload)
    {
        try { _ctx.Broadcast(ev, El(payload)); }
        catch (Exception ex) { _ctx.Log($"{ev} did not go out ({ex.GetType().Name})"); }
    }

    /// An old sidecar exe without emoji.* hands both chords back to Windows.
    async Task ProbeAsync()
    {
        PlatformReply r = await _ctx.Platform.CallAsync("emoji.verdict", new JsonObject { ["vk"] = 0x41 }, TimeSpan.FromMilliseconds(3000)).ConfigureAwait(true);
        if (_stopped) return;
        _sidecarWhy = r.Ok ? null
            : System.Text.RegularExpressions.Regex.IsMatch(r.Error ?? "", "unknown|no such|not a command", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                ? TooOld : $"the sidecar did not answer ({r.Error})";
        Judge();
    }

    async Task ToggleAsync(string from)
    {
        if (_stopped || !_ctx.Enabled) return;
        if (_open) { Close("again"); return; }
        int seq = ++_seq;
        // what the chord was pressed over; a --test run never reads the real foreground (the panel itself stands in)
        PlatformReply a = _test
            ? PlatformReply.Success(El(new JsonObject { ["hwnd"] = _panelHwnd, ["app"] = "mantle-test", ["test"] = true }))
            : await _ctx.Platform.CallAsync("emoji.arm", new JsonObject(), TimeSpan.FromMilliseconds(150)).ConfigureAwait(true);
        if (seq != _seq || _stopped) return;
        _hwnd = a.Ok ? Str(a.Result, "hwnd") : null;
        _app = a.Ok ? Str(a.Result, "app") : null;
        if (a.Ok && Bool(a.Result, "elevated") && !Bool(a.Result, "selfElevated")) {
            PlatformReply h = _test ? PlatformReply.Success(El(new JsonObject { ["dryRun"] = true }))
                : await _ctx.Platform.CallAsync("emoji.windows", new JsonObject(), TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true);
            string name = EmojiPicks.AppName(_app) ?? "That window";
            _ctx.Log($"over an administrator window: handed to Windows' panel ({(h.Ok ? "sent" : h.Error)})");
            Send(NoticeEvent, new JsonObject {
                ["text"] = $"{name} runs as administrator, so Mantle can't type into it. " +
                           (h.Ok ? "Windows' emoji panel opened instead." : $"Press Win+. again once you are out of it ({h.Error})."),
                ["ms"] = NoticeMs,
            });
            return;
        }
        var arm = a.Ok ? JsonNode.Parse(a.Result.GetRawText()) : new JsonObject { ["hwnd"] = null, ["error"] = a.Error };
        var show = new JsonObject {
            ["arm"] = arm, ["from"] = from, ["tone"] = _picks.Tone, ["recents"] = Arr(_picks.Recents),
            ["app"] = EmojiPicks.AppName(_app), ["appPicks"] = Arr(_picks.AppPicks(_app)), ["closeAfter"] = _picks.CloseAfterInsert,
            ["notice"] = _hwnd != null ? null : NoWindow,
        };
        _open = true;
        Send(ShowEvent, show);
        _ctx.Log($"opened by {from}");
        await StartWatchAsync(seq).ConfigureAwait(true);
    }

    async Task StartWatchAsync(int seq)
    {
        _kbSelf = false;
        if (_test) return;   // keys come from the test's emoji.key events
        var args = new JsonObject { ["on"] = true, ["hwnd"] = _hwnd, ["panel"] = _panelHwnd, ["leaseMs"] = LeaseMs };
        PlatformReply r = _hwnd != null
            ? await _ctx.Platform.CallAsync("emoji.watch", args, TimeSpan.FromMilliseconds(1500)).ConfigureAwait(true)
            : PlatformReply.Fail(PlatformOutcome.Failed, "no window to type into");
        if (!_open || seq != _seq || _stopped) {
            if (r.Ok) _ = _ctx.Platform.CallAsync("emoji.watch", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500));
            return;
        }
        if (r.Ok) { _watch = true; Renew(seq, args); return; }
        _ctx.Log($"the key watch could not start ({r.Error}); the picker takes the keyboard itself");
        _kbSelf = true;
        Send(KeyboardEvent, new JsonObject { ["self"] = true, ["closeAfter"] = true });
    }

    /// The lease is renewed every 2 s while the panel is open; it lapses by itself 6 s after the last renewal.
    void Renew(int seq, JsonObject args)
    {
        _renew?.Dispose();
        _renew = _clock.Later(TimeSpan.FromMilliseconds(RenewMs), () => {
            _renew = null;
            if (!_open || seq != _seq || _stopped) return;
            _ = _ctx.Platform.CallAsync("emoji.watch", (JsonObject)args.DeepClone(), TimeSpan.FromMilliseconds(1500));
            Renew(seq, args);
        });
    }

    void Close(string why)
    {
        _seq++;
        _renew?.Dispose(); _renew = null;
        if (_watch) { _watch = false; _ = _ctx.Platform.CallAsync("emoji.watch", new JsonObject { ["on"] = false }, TimeSpan.FromMilliseconds(1500)); }
        if (!_open) return;
        _open = false;
        Send(HideEvent, new JsonObject { ["why"] = why });
    }

    static string Cap(string? s)
    {
        s = string.IsNullOrEmpty(s) ? "it was not typed" : s;
        return char.ToUpperInvariant(s[0]) + s.Substring(1);
    }

    async ValueTask<CommandResult> InsertAsync(JsonElement a)
    {
        string ch = Str(a, "ch") ?? "";
        if (ch.Length == 0 || ch.Length > 64) return CommandResult.Success(El(new JsonObject { ["typed"] = false, ["reason"] = "nothing to type" }));
        bool closeNow = Bool(a, "close") || _kbSelf;
        var res = new JsonObject();
        bool typed = false, dry = false, copy = false;
        if (_hwnd == null) {
            copy = true;
            res["reason"] = "Copied: paste it with Ctrl+V.";
        } else {
            PlatformReply r = await _ctx.Platform.CallAsync(_kbSelf && !_test ? "emoji.typeback" : "emoji.type",
                new JsonObject { ["hwnd"] = _hwnd, ["text"] = ch, ["dryRun"] = _test }, TimeSpan.FromMilliseconds(2500)).ConfigureAwait(true);
            if (r.Ok) { typed = !_test; dry = _test; }
            else { copy = true; res["reason"] = $"{Cap(r.Error)}. It is copied: paste it with Ctrl+V."; }
        }
        res["typed"] = typed;
        res["dryRun"] = dry;
        res["copy"] = copy;   // the panel puts the pick on the clipboard (never in a test)
        if (typed || dry || copy) {
            StoreWrite w = await _picks.RememberAsync(ch, _app).ConfigureAwait(true);
            if (!w.Ok) _ctx.Log("the pick could not be saved: " + w.Reason);
        }
        _ctx.Log($"pick: {(typed ? "typed" : dry ? "dry run" : copy ? "copied" : "refused")}{(Str(a, "via") is { } via ? " by " + via : "")}");
        if (closeNow || (!typed && !dry)) Close(typed || dry ? "insert" : "not typed");
        res["recents"] = Arr(_picks.Recents);
        return CommandResult.Success(El(res));
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        Close(stop.Rest ? "rest" : "stop");   // the watch's lease lapses by itself if this call is refused
        _stopped = true;
        return ValueTask.CompletedTask;
    }
}
