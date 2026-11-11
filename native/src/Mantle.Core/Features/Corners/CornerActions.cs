using System;
using System.Threading.Tasks;

namespace Mantle.Core.Features.Corners;

/// <summary>What an effect answered: ok, or not and why; a dry run names what it would have called.</summary>
public sealed record CornerFx(bool Ok, string? Reason = null, bool DryRun = false, string? Called = null, string? WouldCall = null,
    string? Feature = null, bool Closed = false);

/// <summary>The effects a corner action uses (lib/features/corners.js runAction's <c>h</c>). Each gets
/// <c>dry</c> = true in a --test run: it only checks and says what it would have done, never doing it.</summary>
public interface ICornerEffects
{
    /// Run the feature that owns the chord (Win+Tab) as if pressed; dry = ask whether one could take it.
    CornerFx Chord(string chord, bool dry);
    /// Windows' own Show desktop toggle (Shell.Application ToggleDesktop through the sidecar), never Win+D.
    Task<CornerFx> DesktopAsync(bool dry);
    /// Mantle's Start in apps mode with an empty query. Throws (with words) when there is no launcher.
    CornerFx Launcher(bool dry);
    /// Toggle quick settings on the bar (Closed = it was open and closed).
    Task<CornerFx> QsAsync(bool dry);
    /// LockWorkStation.
    CornerFx Lock(bool dry);
}

/// <summary>One action's outcome, in words (<paramref name="Said"/>), never thrown.</summary>
public sealed record CornerOutcome(bool Ok, bool DryRun, string Said);

public static class CornerActions
{
    const string Dry = "DRY RUN (--test): would have ";

    /// runAction: every failure is ok:false with its reason in words; an exception becomes "&lt;Action&gt; failed: msg".
    public static async Task<CornerOutcome> RunAsync(string action, ICornerEffects h, bool test)
    {
        bool dry = test;
        string name = CornerRules.ActionName(action);
        static string Why(string? r) => string.IsNullOrEmpty(r) ? "no reason came back" : r;
        try {
            switch (action) {
                case "taskview": {
                    var r = h.Chord("Win+Tab", dry);
                    if (dry) return r.DryRun && r.Ok ? new(true, true, $"{Dry}opened Task view ({r.Feature} takes Win+Tab)")
                                                     : new(false, true, $"Task view could not open: {Why(r.Reason)}");
                    return r.Ok ? new(true, false, $"opened Task view ({r.Feature}'s Win+Tab)") : new(false, false, $"Task view did not open: {Why(r.Reason)}");
                }
                case "desktop": {
                    var r = await h.DesktopAsync(dry).ConfigureAwait(true);
                    if (!r.Ok) return new(false, dry, $"Show desktop did not run: {Why(r.Reason)}");
                    return dry ? new(true, true, $"{Dry}called {r.WouldCall ?? "Show desktop"}") : new(true, false, $"toggled Show desktop ({r.Called ?? "the shell"})");
                }
                case "launcher": {
                    var r = h.Launcher(dry);
                    if (!r.Ok) return new(false, dry, $"Launcher failed: {Why(r.Reason)}");
                    return dry ? new(true, true, $"{Dry}opened the launcher") : new(true, false, "opened the launcher");
                }
                case "qs": {
                    var r = await h.QsAsync(dry).ConfigureAwait(true);
                    if (!r.Ok) return new(false, dry, $"Quick settings did not open: {Why(r.Reason)}");
                    return dry ? new(true, true, $"{Dry}opened quick settings on the bar") : new(true, false, r.Closed ? "closed quick settings (it was open)" : "opened quick settings");
                }
                case "lock": {
                    var r = h.Lock(dry);
                    if (!r.Ok) return new(false, dry, $"the screen did not lock: {Why(r.Reason)}");
                    return dry ? new(true, true, $"{Dry}called {r.WouldCall ?? "LockWorkStation"}") : new(true, false, "locked the screen");
                }
                default:
                    return new(false, dry, $"\"{action}\" is not a corner action");
            }
        } catch (Exception e) {
            return new(false, dry, $"{name} failed: {e.Message}");
        }
    }
}
