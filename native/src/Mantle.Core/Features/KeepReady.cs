using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;

namespace Mantle.Core.Features;

// lib/keep-ready.js: which app surfaces are kept made-but-hidden so their key opens them instantly.
// Default OFF for every one (the user: "i need to save ram"); each opt-in costs memory all the time
// and is let go while a game runs. keep-ready.json in the profile, keys "keepReady.<surface>".
// A native decision (PLAN.md, windowing spike): park one window per instant pop-up only; these four
// app surfaces open fresh unless the user turns this on.

/// <summary>One surface that can be kept ready.</summary>
public sealed record KeepReadySurface(string Surface, string Label, string Keys, int Mb);

/// <summary>One row for Mantle Settings.</summary>
public sealed record KeepReadyRow(string Surface, string Key, bool On, int Mb, string Label, string Help);

/// <summary>A switch's answer: never thrown, always with words.</summary>
public sealed record KeepReadyAnswer(bool Ok, string? Reason, bool On);

public sealed class KeepReady
{
    public const string FileName = "keep-ready.json";

    /// The surfaces, in Electron's order (Settings, Task Manager, Files, Task View).
    public static readonly IReadOnlyList<KeepReadySurface> Surfaces = new[]
    {
        new KeepReadySurface("settings", "Mantle Settings", "Win+I", 45),
        new KeepReadySurface("taskmgr", "Task Manager", "Ctrl+Shift+Esc", 35),
        new KeepReadySurface("files", "Files", "Win+E", 35),
        new KeepReadySurface("taskview", "Task View", "Win+Tab", 45),
    };

    public static string KeyOf(string surface) => $"keepReady.{surface}";

    static KeepReadySurface? Find(string? s) => Surfaces.FirstOrDefault(x => x.Surface == s);

    /// The label and help Mantle Settings shows for <paramref name="surface"/>, or null for an unknown one.
    public static (string Label, string Help)? Words(string surface)
    {
        var o = Find(surface);
        if (o == null) return null;
        return ($"Keep {o.Label} ready",
            $"On: {o.Keys} opens it instantly, but it holds about {o.Mb} MB of memory all the time (let go while a game runs). " +
            "Off: it opens fresh in well under a second and uses no memory while closed.");
    }

    readonly IStore? _store;
    readonly Action<string> _log;
    readonly List<Action<string, bool>> _fns = new();
    string? _problem;

    /// <param name="store">keep-ready.json, or null when Mantle has no profile folder to keep it in.</param>
    public KeepReady(IStore? store, Action<string>? log = null)
    {
        _store = store;
        _log = log ?? (_ => { });
        if (store == null) _problem = "Mantle has no folder to keep this switch in, so every surface opens fresh";
        else if (store.State is not ReadState.Ok and not ReadState.Missing)
        {
            _problem = $"{FileName} did not hold these switches, so every surface opens fresh ({store.Reason ?? store.State.ToString()})";
            Say($"keep ready: {_problem}");
        }
    }

    void Say(string line) { try { _log(line); } catch { /* never in the way */ } }

    /// Why the file could not be used, or null.
    public string? Reason => _problem;

    /// Only an exact true counts: anything else in the file is off.
    public bool Get(string surface) =>
        Find(surface) != null && _store != null && _store.TryGet(KeyOf(surface), out var v) && v.ValueKind == JsonValueKind.True;

    public IReadOnlyDictionary<string, bool> All() => Surfaces.ToDictionary(s => KeyOf(s.Surface), s => Get(s.Surface));

    public async ValueTask<KeepReadyAnswer> SetAsync(string surface, bool on)
    {
        var o = Find(surface);
        if (o == null)
        {
            var shown = (surface ?? "").Length > 40 ? surface![..40] : surface;
            return new(false, $"\"{shown}\" is not a window Mantle can keep ready ({string.Join(", ", Surfaces.Select(s => s.Surface))})", false);
        }
        var before = Get(surface);
        if (_store == null) return new(false, _problem ?? "Mantle has no folder to keep this switch in", before);
        StoreWrite w;
        try
        {
            // The whole set in one write: the four keys, each true or false, as Electron writes them.
            var next = Surfaces.Select(s => (Key: KeyOf(s.Surface), On: s.Surface == surface ? on : Get(s.Surface))).ToArray();
            w = await _store.UpdateAsync(doc =>
            {
                foreach (var (key, v) in next) doc[key] = JsonValue.Create(v);
            });
        }
        catch (Exception e) { w = StoreWrite.Failed(e.Message); }
        if (!w.Ok) return new(false, $"the switch could not be saved ({w.Reason}), so it stays {(before ? "on" : "off")}", before);
        _problem = null;
        if (before != on)
        {
            Say($"keep ready: {o.Label} {(on ? "kept ready in the background" : "opens fresh (nothing kept)")}");
            foreach (var fn in _fns.ToArray())
            {
                try { fn(surface, on); } catch (Exception e) { Say($"keep ready: a listener threw ({e.Message})"); }
            }
        }
        return new(true, null, on);
    }

    public IDisposable OnChange(Action<string, bool> fn)
    {
        if (fn == null) throw new ArgumentNullException(nameof(fn), "keep-ready OnChange needs a handler");
        _fns.Add(fn);
        return new Off(() => _fns.Remove(fn));
    }

    public IReadOnlyList<KeepReadyRow> View() => Surfaces.Select(s =>
    {
        var w = Words(s.Surface)!.Value;
        return new KeepReadyRow(s.Surface, KeyOf(s.Surface), Get(s.Surface), s.Mb, w.Label, w.Help);
    }).ToArray();

    sealed class Off : IDisposable
    {
        Action? _a;
        public Off(Action a) { _a = a; }
        public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
    }
}
