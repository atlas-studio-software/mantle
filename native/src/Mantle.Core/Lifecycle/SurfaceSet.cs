using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Lifecycle;

// The surface host's logic without UI types (PLAN.md 3.5): Mantle.App adapts each ISurface from
// Mantle.Generated.SurfaceCatalog to ISurfaceInstance and keeps one SurfaceSet on the shell UI thread;
// the unit tests drive it with fakes. Every member of SurfaceSet runs on that UI thread.

/// <summary>One surface as the host sees it. Every member is called on the surface's UI thread.</summary>
public interface ISurfaceInstance
{
    /// Build the window(s), hidden (ISurface.Create with its context).
    void Create();
    void Show(string why);
    void Hide(string why);
    bool Shown { get; }
    void Destroy(string why);
}

/// <summary>A catalog entry without the UI types: the name, made at start or on first use, and how to make one.</summary>
public sealed record SurfaceSpec(string Name, bool AtStart, Func<ISurfaceInstance> Make);

public enum SurfacePhase
{
    /// Not made yet (an on-first-use surface), or destroyed by a rest.
    NotMade,
    /// Made: its windows exist, shown or parked.
    Made,
    /// The last try to make it threw; the next show tries again.
    Failed,
    /// A test-only stand-in in a run that does not make them.
    Skipped,
    /// Mantle is quitting: never made again.
    Closed,
}

/// <summary>One surface now, for <c>surface.list</c>, the log and the tests.</summary>
public sealed record SurfaceInfo(string Name, bool AtStart, SurfacePhase Phase, bool Shown, string Reason);

/// <summary>The answer to a show, hide or toggle: whether it worked, whether the surface is on screen now, and the words.</summary>
public sealed record SurfaceAnswer(bool Ok, string Name, bool Shown, string Said);

public sealed class SurfaceSetOptions
{
    /// A --test run (a test-only stand-in is made only in one, and only with TestSurfaces).
    public bool Test { get; init; }
    /// MANTLE_TEST_SURFACES=1 in a --test run: the "test-" stand-ins are made too.
    public bool TestSurfaces { get; init; }
    public Action<string> Log { get; init; } = _ => { };
}

/// <summary>
/// Every surface of the build: the AtStart ones made at start, hidden and parked (Electron keeps the launcher
/// made and hidden so it opens at once); the others made on first use. All destroyed on a rest (made again on
/// the wake when they are AtStart) and at quit. A name nobody built answers in words, never a silent no-op.
/// UI thread only.
/// </summary>
public sealed class SurfaceSet
{
    /// A surface whose name starts with this is a test stand-in: never made in a live or scratch run.
    public const string TestPrefix = "test-";
    public const string TestSurfacesEnv = "MANTLE_TEST_SURFACES";

    public static bool IsTestOnly(string name) => name.StartsWith(TestPrefix, StringComparison.Ordinal);

    /// The stand-ins are made only in a --test run with MANTLE_TEST_SURFACES=1.
    public static bool TestSurfacesWanted(bool test, Func<string, string?> env) => test && env(TestSurfacesEnv) == "1";

    sealed class Slot
    {
        public required SurfaceSpec Spec;
        public ISurfaceInstance? It;
        public SurfacePhase Phase;
        public string Reason = "not made yet";
    }

    readonly List<Slot> _slots = new();
    readonly Dictionary<string, Slot> _byName = new(StringComparer.Ordinal);
    readonly SurfaceSetOptions _o;
    string? _rest;
    bool _started;

    public SurfaceSet(IEnumerable<SurfaceSpec> specs, SurfaceSetOptions o)
    {
        _o = o;
        foreach (var s in specs) {
            if (_byName.ContainsKey(s.Name)) { Log($"surface {s.Name}: named twice in the catalog; the second is ignored"); continue; }
            var slot = new Slot { Spec = s };
            if (IsTestOnly(s.Name) && !(o.Test && o.TestSurfaces)) {
                slot.Phase = SurfacePhase.Skipped;
                slot.Reason = o.Test
                    ? $"{s.Name} is a test stand-in; this --test run did not ask for it ({TestSurfacesEnv}=1)"
                    : $"{s.Name} is a test stand-in, never made outside a --test run";
            }
            _slots.Add(slot);
            _byName[s.Name] = slot;
        }
    }

    void Log(string line) { try { _o.Log(line); } catch (Exception) { } }

    /// Every name in the catalog, in its order (stand-ins included).
    public IReadOnlyList<string> Names => _slots.Select(s => s.Spec.Name).ToArray();

    /// Is there a surface of this name that this run can make?
    public bool Has(string name) => _byName.TryGetValue(name, out var s) && s.Phase != SurfacePhase.Skipped;

    public bool Resting => _rest != null;
    public bool Closed { get; private set; }

    public IReadOnlyList<SurfaceInfo> List()
        => _slots.Select(s => new SurfaceInfo(s.Spec.Name, s.Spec.AtStart, s.Phase, s.Phase == SurfacePhase.Made && SafeShown(s), s.Reason)).ToArray();

    /// One line for the log at start: what this build has, what is made now and what waits for first use.
    public string Describe()
    {
        if (_slots.Count == 0) return "surfaces: none in this build (the bar is made on its own path)";
        string Of(IEnumerable<Slot> l) { var a = l.Select(s => s.Spec.Name).ToArray(); return a.Length == 0 ? "none" : string.Join(", ", a); }
        var usable = _slots.Where(s => s.Phase != SurfacePhase.Skipped).ToArray();
        var skipped = _slots.Where(s => s.Phase == SurfacePhase.Skipped).ToArray();
        return $"surfaces: {_slots.Count} in this build; made at start: {Of(usable.Where(s => s.Spec.AtStart))}; on first use: {Of(usable.Where(s => !s.Spec.AtStart))}" +
               (skipped.Length > 0 ? $"; test stand-ins not made in this run: {Of(skipped)}" : "");
    }

    /// Make every AtStart surface, hidden and parked. Once; a second call says so.
    public void Start(string why)
    {
        if (_started) { Log($"surfaces: already started; {why} changes nothing"); return; }
        _started = true;
        Log(Describe());
        if (Closed) return;
        foreach (var s in _slots) if (s.Spec.AtStart && s.Phase == SurfacePhase.NotMade) Make(s, "at start, hidden and parked");
    }

    bool Make(Slot s, string how)
    {
        ISurfaceInstance? it = null;
        try {
            it = s.Spec.Make();
            it.Create();
            s.It = it; s.Phase = SurfacePhase.Made; s.Reason = "made " + how;
            Log($"surface {s.Spec.Name}: made {how}");
            return true;
        } catch (Exception e) {
            s.It = null; s.Phase = SurfacePhase.Failed;
            s.Reason = $"could not be made ({how}): {e.Message}";
            Log($"surface {s.Spec.Name}: {s.Reason}");
            if (it != null) { try { it.Destroy("it failed to be made"); } catch (Exception) { } }
            return false;
        }
    }

    static bool SafeShown(Slot s) { try { return s.It?.Shown ?? false; } catch (Exception) { return false; } }

    /// Why <paramref name="name"/> cannot be used now, or null when it can.
    string? Refusal(string name, string verb, out Slot? slot)
    {
        if (!_byName.TryGetValue(name, out slot))
            return $"there is no {name} surface in this build, so nothing was {verb}" +
                   (_slots.Count == 0 ? " (this build has no surfaces yet)" : $" (it has: {string.Join(", ", Names)})");
        if (slot.Phase == SurfacePhase.Skipped) return slot.Reason;
        if (Closed) return $"Mantle is quitting, so {name} was not {verb}";
        return null;
    }

    public SurfaceAnswer Show(string name, string why)
    {
        if (Refusal(name, "shown", out var s) is { } no) { Log($"surface {name}: not shown ({why}): {no}"); return new(false, name, false, no); }
        if (_rest != null) {
            string r = $"Mantle is resting while a game runs ({_rest}), so {name} was not shown";
            Log($"surface {name}: {r}");
            return new(false, name, false, r);
        }
        if (s!.Phase != SurfacePhase.Made && !Make(s, $"on first use ({why})")) return new(false, name, false, s.Reason);
        try {
            s.It!.Show(why);
        } catch (Exception e) {
            string r = $"{name} could not be shown ({why}): {e.Message}";
            Log($"surface {name}: {r}");
            return new(false, name, SafeShown(s), r);
        }
        bool on = SafeShown(s);
        Log($"surface {name}: shown ({why})" + (on ? "" : "; it says it is not on screen"));
        return new(true, name, on, on ? $"{name} is shown" : $"{name} was asked to show but says it is not on screen");
    }

    public SurfaceAnswer Hide(string name, string why)
    {
        if (Refusal(name, "hidden", out var s) is { } no) {
            // quitting or a stand-in: nothing is on screen, which is what a hide wants
            bool fine = s != null;
            if (!fine) Log($"surface {name}: not hidden ({why}): {no}");
            return new(fine, name, false, no);
        }
        if (s!.Phase != SurfacePhase.Made) return new(true, name, false, $"{name} is not made, so it is already hidden");
        try {
            s.It!.Hide(why);
        } catch (Exception e) {
            string r = $"{name} could not be hidden ({why}): {e.Message}";
            Log($"surface {name}: {r}");
            return new(false, name, SafeShown(s), r);
        }
        bool on = SafeShown(s);
        Log($"surface {name}: hidden ({why})" + (on ? "; it says it is still on screen" : ""));
        return new(!on, name, on, on ? $"{name} was asked to hide but says it is still on screen" : $"{name} is hidden");
    }

    public SurfaceAnswer Toggle(string name, string why)
        => _byName.TryGetValue(name, out var s) && s.Phase == SurfacePhase.Made && SafeShown(s) ? Hide(name, why) : Show(name, why);

    public SurfaceAnswer Run(string verb, string name, string why) => verb switch {
        "show" => Show(name, why),
        "hide" => Hide(name, why),
        "toggle" => Toggle(name, why),
        _ => new(false, name, false, $"\"{verb}\" is not something a surface does (show, hide, toggle)"),
    };

    /// A rest while a game runs (main.js restNow): every surface destroyed; the AtStart ones come back on the wake.
    /// The names of the surfaces it destroyed.
    public IReadOnlyList<string> Rest(string why)
    {
        _rest = why;
        return DestroyAll($"a rest ({why})", SurfacePhase.NotMade, "destroyed for a rest: " + why);
    }

    /// The rest is over: the AtStart surfaces are made again (hidden); the others wait for their next use.
    public IReadOnlyList<string> Wake(string why)
    {
        if (_rest == null) return Array.Empty<string>();
        _rest = null;
        var made = new List<string>();
        if (Closed || !_started) return made;
        foreach (var s in _slots)
            if (s.Spec.AtStart && (s.Phase is SurfacePhase.NotMade or SurfacePhase.Failed) && Make(s, $"again after the rest ({why}), hidden and parked")) made.Add(s.Spec.Name);
        return made;
    }

    /// Quit: every surface destroyed, and none is made again.
    public IReadOnlyList<string> Close(string why)
    {
        Closed = true;
        return DestroyAll(why, SurfacePhase.Closed, "destroyed at quit: " + why);
    }

    IReadOnlyList<string> DestroyAll(string why, SurfacePhase to, string reason)
    {
        var gone = new List<string>();
        foreach (var s in _slots) {
            if (s.Phase == SurfacePhase.Skipped) continue;
            var it = s.It;
            s.It = null;
            if (it != null) {
                try { it.Destroy(why); Log($"surface {s.Spec.Name}: destroyed ({why})"); }
                catch (Exception e) { Log($"surface {s.Spec.Name}: destroying it threw ({why}): {e.Message}"); }
                gone.Add(s.Spec.Name);
            }
            s.Phase = to;
            s.Reason = reason;
        }
        return gone;
    }
}
