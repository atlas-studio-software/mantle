using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Keys;
using Mantle.Core.Platform;

namespace Mantle.Core.Features;

public sealed partial class FeatureHost
{
    static readonly Regex VerbRe = new("^[a-zA-Z][a-zA-Z0-9_.-]*$", RegexOptions.CultureInvariant);

    /// <summary>One install's context (lib/features.js makeCtx). Everything registered through it is released
    /// when the feature stops, rests or fails to install; after that it refuses new registrations.</summary>
    sealed class Ctx : IFeatureContext, IFeatureHostAccess, IFeatureTestKeys, IFeatureDirectory, IFeatureHostServices
    {
        readonly FeatureHost _h;
        readonly F _f;
        readonly int _gen;
        readonly ScopedPlatform _platform;

        public Ctx(FeatureHost h, F f, int gen, CancellationToken stopping)
        {
            _h = h; _f = f; _gen = gen; Stopping = stopping;
            _platform = new ScopedPlatform(h, f, this);
        }

        public string Name => _f.Name;
        public FeatureFlags Flags => _h._flags;
        public CancellationToken Stopping { get; }
        public IPlatform Platform => _platform;
        public bool Enabled => _f.Enabled;
        public IStore Settings => _h.Store(_f);
        public IFeatureDirectory Features => this;
        public IFeatureTestKeys? Test => _h._flags.Test ? this : null;

        /// False once the feature stopped, rested, failed or was restarted: this context adds nothing more.
        internal bool Alive => _f.Gen == _gen && !_f.Stopped;

        internal void MustBeAlive(string what)
        {
            if (_f.Gen != _gen) throw new InvalidOperationException($"{_f.Name}: {what} after the feature was restarted or failed; this context is no longer in use");
            if (_f.Stopped) throw new InvalidOperationException($"{_f.Name}: {what} after the feature was stopped; this context is no longer in use");
        }

        public void Log(string text) => _h.Log($"{_f.Name}: {FeatureRules.OneLine(text)}");

        public IDisposable Command(string verb, CommandHandler handler)
        {
            if (verb == null || !VerbRe.IsMatch(verb)) throw new ArgumentException($"{_f.Name}: command verb \"{verb}\" is not a plain word");
            if (handler == null) throw new ArgumentNullException(nameof(handler), $"{_f.Name}: command \"{verb}\" needs a handler");
            MustBeAlive($"Command(\"{verb}\")");
            var full = $"{_f.Name}.{verb}";
            var f = _f;
            var label = f.Label;
            async ValueTask<CommandResult> Wrapped(CommandCall call)
            {
                if (f.Resting != null) return CommandResult.Fail($"{label} is {FeatureRules.RestWords(f.Resting)}; it is back when the game exits");
                if (f.Stopped) return CommandResult.Fail($"{label} is stopped (Mantle put Windows back)");
                try { return await handler(call); }
                catch (Exception e) { return CommandResult.Fail($"{full} failed: {Words(e)}"); }
            }
            var reg = _h._o.Commands.Register(new CommandRegistration(full, $"the {f.Name} feature", Wrapped));
            var undo = new Undo(() => { reg.Dispose(); if (f.Commands.TryGetValue(full, out var cur) && ReferenceEquals(cur, reg)) f.Commands.Remove(full); });
            f.Commands[full] = reg;
            return undo;
        }

        IDisposable Subscribe<T>(Dictionary<string, List<T>> map, string key, T fn, string what)
        {
            if (fn == null) throw new ArgumentNullException(nameof(fn), $"{_f.Name}: {what} needs a handler");
            MustBeAlive(what);
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<T>();
            list.Add(fn);
            return new Undo(() => list.Remove(fn));
        }

        public IDisposable OnChord(string chord, Action<string> handler)
        {
            if (!_f.Chords.Contains(chord)) throw new ArgumentException($"{_f.Name}: OnChord(\"{chord}\") but it does not list \"{chord}\" in Chords");
            return Subscribe(_f.ChordHandlers, chord, handler, "OnChord");
        }

        public IDisposable OnKey(string key, Action<string> handler)
        {
            if (!_f.Observe.Contains(key)) throw new ArgumentException($"{_f.Name}: OnKey(\"{key}\") but it does not list \"{key}\" in Observe");
            return Subscribe(_f.KeyHandlers, key, handler, "OnKey");
        }

        public void Health(bool ok, string? reason = null) { if (Alive) _h.SetHealth(_f, ok, reason); }

        public IDisposable OnEnabled(Action<bool> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            MustBeAlive("OnEnabled");
            _f.EnabledHandlers.Add(handler);
            var list = _f.EnabledHandlers;
            return new Undo(() => list.Remove(handler));
        }

        public IDisposable OnOption(string key, Action<JsonElement, string> handler)
        {
            if (key != "*" && !OptionsOf(_f).Any(o => o.Key == key)) throw new ArgumentException($"{_f.Name}: OnOption(\"{key}\") but it declares no option \"{key}\"");
            return Subscribe(_f.OptionHandlers, key, handler, "OnOption");
        }

        public void Broadcast<T>(string ev, T payload)
        {
            if (ev == null || !ev.StartsWith(_f.Name + ".", StringComparison.Ordinal)) throw new ArgumentException($"{_f.Name}: its events must start with \"{_f.Name}.\"");
            _h._o.Events.Publish(ev, payload);
        }

        public IDisposable Replay<T>(string ev, Func<T> current)
        {
            if (ev == null || !ev.StartsWith(_f.Name + ".", StringComparison.Ordinal)) throw new ArgumentException($"{_f.Name}: replay events must start with \"{_f.Name}.\"");
            if (current == null) throw new ArgumentNullException(nameof(current));
            MustBeAlive("Replay");
            if (_f.Replays.Remove(ev, out var old)) old.Dispose();
            var f = _f;
            var h = _h;
            var reg = _h._o.Events.Replay(ev, () =>
            {
                try { return current(); }
                catch (Exception e) { h.Log($"{f.Name}: replay(\"{ev}\") threw: {Words(e)}"); throw; }
            });
            _f.Replays[ev] = reg;
            return new Undo(() => { reg.Dispose(); if (f.Replays.TryGetValue(ev, out var cur) && ReferenceEquals(cur, reg)) f.Replays.Remove(ev); });
        }

        // ---- IFeatureHostAccess ----

        public IDisposable OnRest(Action<RestSignal> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            MustBeAlive("OnRest");
            _f.RestHandlers.Add(handler);
            var list = _f.RestHandlers;
            return new Undo(() => list.Remove(handler));
        }

        public void KeysAway(string? why)
        {
            if (!Alive) return;
            var next = string.IsNullOrWhiteSpace(why) ? null : why;
            if (next == _f.KeysAway) return;
            _f.KeysAway = next;
            _h.Publish(_f);
            _h.ScheduleApply(next != null ? $"{_f.Name} hands its keys back ({next})" : $"{_f.Name} takes its keys again");
        }

        public KeyDelivery RunChord(string chord, string via, bool dryRun = false) => _h.RunChordFor(_f, chord, via ?? "no reason given", dryRun);

        public bool HasCommand(string name) => _h._o.Commands.Has(name);

        public GameWatch GameWatch => _h.GameWatch;

        public Task<RestAnswer> RestAsync(bool on, string why)
        {
            if (!Alive) return Task.FromResult(new RestAnswer(false, $"{_f.Label} is stopped, so it cannot rest Mantle"));
            return _h.RestHost?.RestAsync(on, why) ?? Task.FromResult(new RestAnswer(false, "resting for a game is not part of this build yet"));
        }

        public KeyDelivery FireChord(string chord) => _h.FireChord(chord, $"{_f.Name} (test)");
        public KeyDelivery FireKey(string key) => _h.FireKey(key, $"{_f.Name} (test)");

        // ---- IFeatureDirectory (Mantle Settings' road; every write is logged "by <label>") ----

        IReadOnlyList<FeatureInfo> IFeatureDirectory.List() => _h.List();
        FeatureAnswer IFeatureDirectory.SetEnabled(string name, bool on) => _h.TrySetEnabled(name, on, _f.Label);
        ValueTask<FeatureAnswer> IFeatureDirectory.SetOptionAsync(string name, string key, JsonElement value) => _h.SetOptionAsync(name, key, value, _f.Label);
        ValueTask<FeatureAnswer> IFeatureDirectory.RestartAsync(string name) => new(_h.RestartAsync(name, _f.Label));

        IDisposable IFeatureDirectory.OnChange(Action<string, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            MustBeAlive("Features.OnChange");
            var f = _f;
            void Wrapped(string what, string name) { if (!f.Stopped) handler(what, name); }
            _h._changeFns.Add(Wrapped);
            var undo = new Undo(() => _h._changeFns.Remove(Wrapped));
            f.Offs.Add(undo);
            return undo;
        }
    }

    /// <summary>The platform as one install sees it: 4 s default, never throws, subscriptions end at the
    /// stop, calls after the stop are refused, and a handler that throws makes the feature unhealthy.</summary>
    sealed class ScopedPlatform : IPlatform
    {
        readonly FeatureHost _h;
        readonly F _f;
        readonly Ctx _ctx;

        public ScopedPlatform(FeatureHost h, F f, Ctx ctx) { _h = h; _f = f; _ctx = ctx; }

        public PlatformStatus Status => _h._o.Platform.Status;

        public async ValueTask<PlatformReply> CallAsync(string verb, JsonObject? args = null, TimeSpan? timeout = null, CancellationToken cancel = default)
        {
            if (!_ctx.Alive) return PlatformReply.Fail(PlatformOutcome.Refused, $"{_f.Label} is stopped, so it asks Windows for nothing more");
            try { return await _h._o.Platform.CallAsync(verb, args, timeout ?? PlatformDefaults.FeatureCallTimeout, cancel); }
            catch (Exception e) { return PlatformReply.Fail(PlatformOutcome.Failed, $"{verb} threw: {Words(e)}"); }
        }

        IDisposable Track(Func<IDisposable> subscribe, string what)
        {
            _ctx.MustBeAlive(what);
            var inner = subscribe();
            var f = _f;
            Undo? undo = null;
            undo = new Undo(() => { inner.Dispose(); f.Offs.Remove(undo!); });
            f.Offs.Add(undo);
            return undo;
        }

        public IDisposable On(string ev, Action<PlatformEvent> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Track(() => _h._o.Platform.On(ev, e => { if (_ctx.Alive) _h.RunHandler(_f, $"platform \"{ev}\"", () => handler(e)); }), $"Platform.On(\"{ev}\")");
        }

        public IDisposable OnStatus(Action<PlatformStatus> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Track(() => _h._o.Platform.OnStatus(s => { if (_ctx.Alive) _h.RunHandler(_f, "platform status", () => handler(s)); }), "Platform.OnStatus");
        }

        public IDisposable OnReady(Action handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return Track(() => _h._o.Platform.OnReady(() => { if (_ctx.Alive) _h.RunHandler(_f, "platform ready", handler); }), "Platform.OnReady");
        }
    }

    sealed class Undo : IDisposable
    {
        Action? _undo;
        public Undo(Action undo) { _undo = undo; }
        public void Dispose() { Interlocked.Exchange(ref _undo, null)?.Invoke(); }
    }
}
