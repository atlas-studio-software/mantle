using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;

namespace Mantle.Core.Features.TopBar;

// The window and the session (lib/features/topbar.js): the window fight's health (TOP-230; the surface does the
// put-back, TopBarSurface.Drift.cs), Explorer restarting (TOP-250: the surface hears TaskbarCreated on the bar's own
// window, no timer) and Windows ending the session (TOP-258).
public sealed partial class TopBarFeature
{
    string? _driftWhy;
    static TopBarFeature? _current;

    /// Read by the tests: the window fight's reason as the surface last said it (null = none).
    public string? DriftWhy => _driftWhy;
    public bool Ending => _ending;

    void InstallLifecycle(IFeatureContext ctx)
    {
        _current = this;
        ctx.Command("drift", c =>
        {
            var was = _driftWhy;
            _driftWhy = Bool(c.Args, "stuck") ? Str(c.Args, "reason") : null;
            if (was != _driftWhy) Health();
            return Ok(new JsonObject { ["ok"] = true });
        });
        // TOP-250: a new Explorer knows no app bars; the strip is asked for again (the work-area check that follows
        // registers it afresh when Explorer ignored it, TOP-249)
        ctx.Command("shell", async c =>
        {
            if (_stopped || _ending || !_ctx.Enabled) return Answer(new JsonObject { ["ok"] = true, ["reserve"] = false });
            await ReserveStrip(Str(c.Args, "why") ?? "Explorer restarted");
            return Answer(new JsonObject { ["ok"] = true, ["reserve"] = true });
        });
    }

    /// The window fight, while the bar is shown (a fight pauses while it is hidden).
    string? DriftHealth() => _shown ? _driftWhy : null;

    /// The app's session window heard WM_ENDSESSION (ending): the running top bar lets go now (App.xaml.cs, one line).
    public static void SessionEnding() => _ = _current?.SessionEnd();

    /// TOP-258: Windows is ending the session: the strip is let go now and never taken again by this run (display
    /// changes and pending re-checks find _ending), and the game loop stops.
    public async Task SessionEnd()
    {
        if (_ending || _stopped) return;
        _ending = true;
        RunLoopStop();
        if (!_live || _hwnd == System.IntPtr.Zero) { _ctx.Log("Windows is ending the session: a --test instance holds no strip, so there was none to let go"); return; }
        _ctx.Log("Windows is ending the session: the top bar let go of its strip now, so the next start finds none left over");
        var r = await ReleaseStrip("Windows is ending the session");
        if (!r.Ok) _ctx.Log($"app bar: letting go at session end failed: {r.Reason}");
    }
}
