using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Snap;

/// <summary>
/// WINDOW SNAPPING (lib/features/snap.js), lane r9-snap: the Win+arrow half. A chord moves the foreground window through
/// the sidecar's snap.move (sidecar/Snap.cs, linked unchanged, PLAN A4) by <see cref="SnapCore"/>, which also puts a
/// window that landed off every live monitor back on one (<see cref="SnapSafety"/>). Notices go out as
/// <see cref="NoticeEvent"/> {title, text, work} for the snap surface. Health: the start-up probe (snap.monitors, 3 s,
/// asked again at 2, 5, 15 and 30 s on a timeout); any answered chord also counts as healthy; while it is not healthy the
/// host hands the chords back to Windows.
/// DEVIATION (until the picker / Snap Assist surface exists): Win+Z is NOT claimed, so Windows' own layout flyout
/// answers it instead of a Win+Z that would do nothing. snap.js claims five chords (SNAP-001).
/// </summary>
[Feature("snap", "Window snapping (Win+arrows, Win+Z)", true, Chords = new[] { "Win+Left", "Win+Right", "Win+Up", "Win+Down" }, Rest = true)]
public sealed class SnapFeature : IFeature
{
    public const string NoticeEvent = "snap.notice";

    IFeatureContext _ctx = null!;
    SnapCore _core = null!;
    SynchronizationContext? _host;
    Timer? _probeTimer;
    int _probeGen;
    bool _stopped, _healthy;

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _host = SynchronizationContext.Current;
        _core = new SnapCore((verb, args, timeout) => ctx.Platform.CallAsync(verb, args, timeout), ctx.Log, Publish, ctx.Flags.Test);
        foreach (string chord in SnapCore.Dirs.Keys) ctx.OnChord(chord, c => _ = ArrowAsync(c));
        ctx.Platform.OnReady(() => _ = ProbeAsync(0));
        return ValueTask.CompletedTask;
    }

    void Publish(SnapNotice n)
    {
        if (_stopped) return;
        var o = new JsonObject { ["title"] = n.Title, ["text"] = n.Text };
        if (n.Work is { } w) o["work"] = new JsonObject { ["x"] = w.Left, ["y"] = w.Top, ["w"] = w.Width, ["h"] = w.Height };
        _ctx.Broadcast(NoticeEvent, El(o));
    }

    void StopProbe()
    {
        _probeGen++;
        _probeTimer?.Dispose();
        _probeTimer = null;
    }

    async Task ProbeAsync(int attempt)
    {
        _probeTimer?.Dispose();
        _probeTimer = null;
        if (_stopped) return;
        int gen = ++_probeGen;
        PlatformReply r = await _ctx.Platform.CallAsync("snap.monitors", new JsonObject(), SnapCore.ProbeTimeout).ConfigureAwait(true);
        if (_stopped || gen != _probeGen) return;
        SnapProbe p = SnapCore.Probe(r, attempt);
        _healthy = p.Ok;
        _ctx.Health(p.Ok, p.Reason);
        if (p.RetryAfter is { } after) {
            var host = _host;
            _probeTimer = new Timer(state => {
                void Go() => _ = ProbeAsync(attempt + 1);
                if (host != null) host.Post(_ => Go(), null); else Go();
            }, null, after, Timeout.InfiniteTimeSpan);
        }
    }

    async Task ArrowAsync(string chord)
    {
        if (_stopped || !_ctx.Enabled) return;
        try {
            SnapArrowResult res = await _core.ArrowAsync(chord).ConfigureAwait(true);
            if (res.Reply is { Ok: true } && !_healthy && !_stopped) {
                StopProbe();
                _healthy = true;
                _ctx.Health(true);
            }
        } catch (Exception ex) {
            _ctx.Log($"{chord}: the snap failed inside Mantle ({ex.GetType().Name}: {ex.Message})");
        }
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        _stopped = true;
        StopProbe();
        return ValueTask.CompletedTask;
    }
}
