using System;
using System.Collections.Generic;

namespace Mantle.Core.Commands;

/// <summary>
/// Host-to-surface events with current-value replay (IEvents; Electron's broadcast + mantle:replay,
/// LOG-015 / LOG-016). A subscriber whose payload type does not match the publisher's is told in the
/// log with both type names and is not called: never a silent drop. Host thread only.
/// </summary>
public sealed class EventHub : IEvents
{
    readonly Dictionary<string, List<Sub>> _subs = new(StringComparer.Ordinal);
    readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    readonly Action<string> _log;
    readonly HashSet<string> _mismatchSaid = new(StringComparer.Ordinal);

    public EventHub(Action<string> log) { _log = log; }

    sealed record Sub(Type Type, Action<object?> Invoke);
    sealed record Source(Type Type, Func<object?> Current);

    public void Publish<T>(string name, T payload)
    {
        if (!_subs.TryGetValue(name, out var list) || list.Count == 0) return;
        foreach (var s in list.ToArray()) Deliver(name, s, payload, typeof(T));
    }

    public IDisposable Subscribe<T>(string name, Action<T> handler)
    {
        if (!_subs.TryGetValue(name, out var list)) _subs[name] = list = new List<Sub>();
        var sub = new Sub(typeof(T), o => handler((T)o!));
        list.Add(sub);
        if (_sources.TryGetValue(name, out var src)) {
            object? now;
            try { now = src.Current(); }
            catch (Exception e) { _log($"mantle-native: the current value of \"{name}\" could not be read for a new subscriber ({e.Message})"); now = null; }
            if (now != null || !src.Type.IsValueType) Deliver(name, sub, now, src.Type);
        }
        return new Undo(() => list.Remove(sub));
    }

    public IDisposable Replay<T>(string name, Func<T> current)
    {
        if (_sources.TryGetValue(name, out var taken))
            throw new InvalidOperationException($"\"{name}\" already has a replay source ({taken.Type.Name}), so a second one ({typeof(T).Name}) was refused");
        var src = new Source(typeof(T), () => current());
        _sources[name] = src;
        return new Undo(() => { if (_sources.TryGetValue(name, out var now) && ReferenceEquals(now, src)) _sources.Remove(name); });
    }

    /// Has the name a replay source (main.js replayAnswer {has:false} for an unknown name)?
    public bool HasReplay(string name) => _sources.ContainsKey(name);

    void Deliver(string name, Sub s, object? payload, Type published)
    {
        if (!s.Type.IsAssignableFrom(published)) {
            string key = name + "|" + s.Type.FullName + "|" + published.FullName;
            if (_mismatchSaid.Add(key))
                _log($"mantle-native: \"{name}\" is published as {published.Name} but a subscriber expects {s.Type.Name}, so that subscriber was not called (a fault in Mantle)");
            return;
        }
        try { s.Invoke(payload); }
        catch (Exception e) { _log($"mantle-native: a \"{name}\" subscriber failed ({e.GetType().Name}: {e.Message})"); }
    }

    sealed class Undo : IDisposable
    {
        Action? _undo;
        public Undo(Action undo) { _undo = undo; }
        public void Dispose() { var u = _undo; _undo = null; u?.Invoke(); }
    }
}
