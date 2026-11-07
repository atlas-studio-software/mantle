using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Features;

public sealed partial class FeatureHost
{
    // ---- the whole set ----

    /// <summary>
    /// Take the features (the generated catalog, or a test's own list), check each against the contract,
    /// claim their chords, and start every install. Sorted by name, as Electron loads its files. A
    /// descriptor that breaks the contract, clashes with a command or a chord, or repeats a name is that
    /// feature DOWN with the reason; the rest carry on. Returns the names whose install started.
    /// </summary>
    public IReadOnlyList<string> Load(IEnumerable<FeatureDescriptor?> descriptors)
    {
        var list = (descriptors ?? Array.Empty<FeatureDescriptor?>()).ToList();
        list.Sort((a, b) => string.CompareOrdinal(a?.Name, b?.Name));
        var n = 0;
        foreach (var d in list)
        {
            n++;
            var bad = FeatureRules.Validate(d);
            var name = d?.Name is string s && s.Length > 0 ? s : $"feature{n}";
            if (_features.ContainsKey(name))
            {
                Log($"features: a second feature is called \"{name}\"; only the first is used");
                continue;
            }
            if (bad != null)
            {
                var f0 = Add(new F(name, d?.Label is string l && l.Trim().Length > 0 ? l : name));
                Fail(f0, $"the feature \"{name}\" breaks the feature contract: {bad}");
                continue;
            }
            var f = Add(new F(d!.Name, d.Label));
            f.D = d;
            f.Chords = d.Chords.ToArray();
            f.Observe = d.Observe.ToArray();
            f.Rest = d.Rest;
            f.Enabled = _switches.TryGet(f.Name, out var sw) && sw.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? sw.ValueKind == JsonValueKind.True : d.DefaultOn;
            var clash = _o.Commands.Names.FirstOrDefault(k => k.StartsWith(f.Name + ".", StringComparison.Ordinal));
            if (clash != null) { f.Blocked = $"its name clashes with Mantle's own command \"{clash}\""; Fail(f, f.Blocked); continue; }
            var taken = f.Chords.FirstOrDefault(_claims.ContainsKey);
            if (taken != null) { f.Blocked = $"{taken} is already taken by {_claims[taken]}"; Fail(f, f.Blocked); continue; }
            foreach (var c in f.Chords) _claims[c] = f.Name;
        }
        foreach (var name in _order.ToArray())
        {
            var f = _features[name];
            if (f.D != null && f.Ok && f.Blocked == null) _installs.Add(InstallOne(f));
        }
        PublishAll();
        var up = _order.Where(x => _features[x].Running).ToArray();
        var down = _order.Where(x => !_features[x].Running).ToArray();
        Log($"features: {(up.Length > 0 ? "started " + string.Join(", ", up) : "none started")}{(down.Length > 0 ? "; NOT started: " + string.Join(", ", down) : "")}");
        ScheduleApply("the features started");
        return up;
    }

    F Add(F f) { _features[f.Name] = f; _order.Add(f.Name); return f; }

    void Fail(F f, string reason)
    {
        f.Ok = false;
        f.Reason = FeatureRules.OneLine(reason);
        f.Running = false;
        Log($"{f.Name}: NOT started: {f.Reason}");
        Publish(f);
    }

    /// Everything a feature hooked up through its context, let go (subscriptions, handlers); commands stay.
    void Unhook(F f)
    {
        foreach (var off in f.Offs.ToArray()) { try { off.Dispose(); } catch { /* already gone */ } }
        f.Offs.Clear();
        f.ChordHandlers.Clear(); f.KeyHandlers.Clear(); f.EnabledHandlers.Clear(); f.RestHandlers.Clear(); f.OptionHandlers.Clear();
    }

    void DropCommands(F f)
    {
        foreach (var c in f.Commands.Values.ToArray()) { try { c.Dispose(); } catch { /* gone */ } }
        f.Commands.Clear();
    }

    void DropReplays(F f)
    {
        foreach (var r in f.Replays.Values.ToArray()) { try { r.Dispose(); } catch { /* gone */ } }
        f.Replays.Clear();
    }

    /// Everything, commands and replays too; an old context still holding on can add nothing more.
    void DropAll(F f)
    {
        Cancel(f);
        Unhook(f);
        DropCommands(f);
        DropReplays(f);
        f.Gen++;
    }

    void Cancel(F f)
    {
        var cts = f.Cts;
        f.Cts = null;
        if (cts == null) return;
        try { cts.Cancel(); } catch (Exception e) { Log($"{f.Name}: a Stopping callback threw: {Words(e)}"); }
    }

    /// An instance whose install finished after its feature was stopped (a slow install racing the quit or a game).
    void StopLate(F f, IFeature inst)
    {
        Log($"{f.Name}: its install finished after the feature was stopped; stopping what it started");
        _ = Settle(() => inst.StopAsync(new FeatureStop("it finished starting after Mantle stopped it", false)),
            e => Log($"{f.Name}: that stop failed: {Words(e)}"));
    }

    static async Task Settle(Func<ValueTask> run, Action<Exception> failed)
    {
        try { await run(); }
        catch (Exception e) { failed(e); }
    }

    /// <summary>Make a fresh instance and run its install, under the deadline. The task never faults.</summary>
    Task InstallOne(F f)
    {
        var gen = f.Gen;
        IFeature inst;
        try { inst = f.D!.Create(); }
        catch (Exception e)
        {
            DropAll(f);
            Fail(f, $"it could not start: making it threw {Words(e)}");
            return Task.CompletedTask;
        }
        f.Cts = new CancellationTokenSource();
        var ctx = new Ctx(this, f, gen, f.Cts.Token);
        ValueTask vt;
        try { vt = inst.InstallAsync(ctx); }
        catch (Exception e)
        {
            DropAll(f);
            Fail(f, $"it could not start: install() threw {Words(e)}");
            Log($"{f.Name}: {e}");
            return Task.CompletedTask;
        }
        if (vt.IsCompleted)
        {
            Exception? ex = null;
            try { vt.GetAwaiter().GetResult(); } catch (Exception e) { ex = e; }
            if (ex != null)
            {
                DropAll(f);
                Fail(f, $"it could not start: install() threw {Words(ex)}");
                Log($"{f.Name}: {ex}");
                return Task.CompletedTask;
            }
            f.Running = true;
            if (f.Reason == "starting") f.Reason = "working";
            Took(f, inst, gen);
            Publish(f);
            return Task.CompletedTask;
        }
        f.Running = true;
        if (f.Reason == "starting") f.Reason = "working";
        var settled = new TaskCompletionSource(TaskCreationOptions.None);
        var deadline = _clock.Later(_o.InstallDeadline, () =>
        {
            if (f.Handle == null && f.Running && f.Gen == gen && !f.Stopped)
            {
                f.LateInstall = true;
                SetHealth(f, false, $"install() did not finish within {FeatureRules.Seconds(_o.InstallDeadline)}");
            }
            settled.TrySetResult();
        });
        _ = Finish();
        Publish(f);
        return settled.Task;

        async Task Finish()
        {
            try
            {
                await vt;
                deadline.Dispose();
                Took(f, inst, gen);
            }
            catch (Exception e)
            {
                deadline.Dispose();
                if (f.Gen == gen && !f.Stopped)
                {
                    DropAll(f);
                    Fail(f, $"it could not start: install() failed: {Words(e)}");
                    ScheduleApply($"{f.Name} failed to start");
                }
            }
            settled.TrySetResult();
        }
    }

    void Took(F f, IFeature inst, int gen)
    {
        if (f.Stopped || f.Gen != gen) { StopLate(f, inst); return; }
        f.Handle = inst;
        if (f.LateInstall)
        {
            f.LateInstall = false;
            if (!f.Ok) SetHealth(f, true, "working");
        }
    }

    /// <summary>One feature's StopAsync under the deadline. Never faults; resolves with the log line.</summary>
    Task<StopLine> StopOne(F f, string why, bool rest)
    {
        f.Stopped = true;
        Cancel(f);
        var t0 = _clock.NowMs;
        var done = new TaskCompletionSource<StopLine>();
        IDisposable? timer = null;
        void Done(string line)
        {
            if (done.Task.IsCompleted) return;
            timer?.Dispose();
            Unhook(f);
            f.Running = false;
            Log($"{f.Name}: {line}");
            Publish(f);
            done.TrySetResult(new StopLine(f.Name, line));
        }
        var ms = (long)_o.StopDeadline.TotalMilliseconds;
        timer = _clock.Later(_o.StopDeadline, () => Done($"stop() did not finish within {ms} ms; Mantle did not wait for it ({why})"));
        var h = f.Handle;
        if (h == null) { Done($"stopped before its install finished; its stop() runs when it does ({why})"); return done.Task; }
        ValueTask vt;
        try { vt = h.StopAsync(new FeatureStop(why, rest)); }
        catch (Exception e) { Done($"stop() threw: {Words(e)} ({why})"); return done.Task; }
        _ = Watch();
        return done.Task;

        async Task Watch()
        {
            try { await vt; Done($"stopped in {_clock.NowMs - t0} ms ({why})"); }
            catch (Exception e) { Done($"stop() failed after {_clock.NowMs - t0} ms: {Words(e)} ({why})"); }
        }
    }

    /// <summary>Every StopAsync at once, each with its own deadline. Never faults, never waits longer than
    /// the deadline (plus a turn); a second call returns the first one's task.</summary>
    public Task<IReadOnlyList<StopLine>> StopAllAsync(string why)
    {
        if (_stopping != null) return _stopping;
        _applyTimer?.Dispose(); _applyTimer = null;
        foreach (var n in _order)
        {
            var f = _features[n];
            if (f.Resting != null) { f.Resting = null; f.Stopped = true; Publish(f); }
        }
        var list = _order.Select(n => _features[n]).Where(f => f.Running && !f.Stopped).ToArray();
        _stopping = All(list.Select(f => StopOne(f, why, false)).ToArray());
        return _stopping;

        static async Task<IReadOnlyList<StopLine>> All(Task<StopLine>[] tasks) => await Task.WhenAll(tasks);
    }

    // ---- resting while a game runs ----
    // A feature marked Rest is STOPPED while a game runs (its windows destroyed, so its memory goes), its
    // chords go back to Windows, and it reads HEALTHY "resting while <game> runs". WakeAllAsync installs
    // it again: the same install as at start-up, so it comes back exactly as Mantle starts it.

    /// The features that stay up hear it (OnRest). Never awaited: a slow or throwing handler cannot hold up the rest.
    void TellRest(bool on, string? why)
    {
        foreach (var n in _order.ToArray())
        {
            var f = _features[n];
            if (f.Rest || !f.Running || f.Stopped || f.Resting != null || f.RestHandlers.Count == 0) continue;
            var sig = new RestSignal(on, why);
            foreach (var fn in f.RestHandlers.ToArray()) RunHandler(f, "OnRest", () => fn(sig));
        }
    }

    /// <summary>Stop every Rest feature for <paramref name="why"/> ("VALORANT runs"). A second call with a
    /// new reason only changes the words. Returns the names that went to rest now.</summary>
    public async Task<IReadOnlyList<string>> RestAllAsync(string why)
    {
        if (_stopping != null) return Array.Empty<string>();
        TellRest(true, why);
        foreach (var n in _order)
        {
            var r = _features[n];
            if (r.Resting != null && r.Resting != why) { r.Resting = why; if (r.RestP == null) r.Reason = FeatureRules.RestWords(why); Publish(r); }
        }
        var list = _order.Select(n => _features[n]).Where(f => f.Rest && f.Running && !f.Stopped && f.Resting == null).ToArray();
        await Task.WhenAll(list.Select(async f =>
        {
            f.Resting = why;
            f.RestP = StopOne(f, FeatureRules.RestWords(why), true);
            await f.RestP;
            f.RestP = null;
            f.Handle = null;
            DropReplays(f);
            f.Ok = true;
            f.Reason = FeatureRules.RestWords(f.Resting ?? why);
            Publish(f);
        }));
        ScheduleApply(FeatureRules.RestWords(why));
        return list.Select(f => f.Name).ToArray();
    }

    /// <summary>The game is over: install every resting feature again. Returns the names that came back.</summary>
    public async Task<IReadOnlyList<string>> WakeAllAsync(string why)
    {
        if (_stopping != null) return Array.Empty<string>();
        TellRest(false, why);
        var back = new List<string>();
        foreach (var f in _order.Select(n => _features[n]).Where(x => x.Resting != null).ToArray())
        {
            if (f.RestP != null) await f.RestP;
            if (_stopping != null) break;
            f.Resting = null;
            f.Stopped = false;
            DropAll(f);
            f.Reason = "starting";
            Log($"{f.Name}: back ({why})");
            _installs.Add(InstallOne(f));
            back.Add(f.Name);
        }
        ScheduleApply($"back ({why})");
        return back;
    }

    /// <summary>
    /// The way back for a feature that is down: stop it (if it runs), let go of everything it hooked up,
    /// and run the same install as start-up, without restarting Mantle. Refused in words for what a
    /// restart cannot fix. Concurrent restarts of one feature share one task. Never faults.
    /// </summary>
    public Task<FeatureAnswer> RestartAsync(string name, string who)
    {
        if (!_features.TryGetValue(name, out var f)) return Task.FromResult(new FeatureAnswer(false, $"Mantle has no feature called \"{name}\""));
        Task<FeatureAnswer> No(string reason) => Task.FromResult(new FeatureAnswer(false, reason, Info(f)));
        if (_stopping != null) return No($"{f.Label} was stopped when Mantle put Windows back; it comes back when Mantle restarts");
        if (f.D == null) return No($"{f.Label} did not load, so it cannot be restarted ({f.Reason})");
        if (f.Blocked != null) return No($"{f.Label} cannot start: {f.Blocked}");
        if (f.Resting != null) return No($"{f.Label} is {FeatureRules.RestWords(f.Resting)}; it comes back by itself when the game exits");
        if (f.Restarting != null) return f.Restarting;
        if (f.Stopped) return No($"{f.Label} was stopped when Mantle put Windows back; it comes back when Mantle restarts");
        var t = Run();
        if (!t.IsCompleted) f.Restarting = t;   // a restart that finished at once has nothing to share
        return t;

        async Task<FeatureAnswer> Run()
        {
            try
            {
                Log($"{f.Name}: restarting (asked by {who})");
                if (f.Running) await StopOne(f, $"restarting, asked by {who}", false);
                if (_stopping != null) return new(false, $"{f.Label} was stopped when Mantle put Windows back; it comes back when Mantle restarts", Info(f));
                DropAll(f);
                f.Stopped = false;
                f.Handle = null;
                f.LateInstall = false;
                f.Ok = true;
                f.Reason = "starting";
                await InstallOne(f);
                ScheduleApply($"{f.Name} restarted");
                var ok = f.Running && f.Ok;
                return new(ok, ok ? null : f.Reason, Info(f));
            }
            catch (Exception e) { return new(false, $"{f.Label} could not be restarted: {Words(e)}", Info(f)); }
            finally { f.Restarting = null; }
        }
    }
}
