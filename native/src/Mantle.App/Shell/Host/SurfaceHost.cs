using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Lifecycle;
using Mantle.Core.Logging;
using Mantle.Core.State;
using Microsoft.UI.Dispatching;

namespace Mantle.App.Shell.Hosting;

/// <summary>
/// The surface host (PLAN.md 3.5, ISurface.cs): every entry of Mantle.Generated.SurfaceCatalog.All, made through one
/// SurfaceSet on the shell UI thread. AtStart surfaces are made at start, hidden and parked; the others on first use;
/// all destroyed on a rest and at quit. Each surface gets its own SurfaceContext. The bar is not one of them yet
/// (BarWindow keeps its own creation path this round). UI thread, except <see cref="PostUi"/>.
/// </summary>
internal sealed class SurfaceHost
{
    readonly DispatcherQueue _q;
    readonly bool _test;
    readonly bool _testSurfaces;
    readonly ILogSink _log;

    public SurfaceHost(DispatcherQueue q, bool test, bool testSurfaces, ILogSink log)
    {
        _q = q; _test = test; _testSurfaces = testSurfaces; _log = log;
    }

    /// Null until <see cref="Start"/> ran.
    public SurfaceSet? Set { get; private set; }

    /// Run on the shell UI thread; false when its queue is gone (Mantle is closing). Any thread.
    public bool PostUi(Action work)
    {
        try { return _q.TryEnqueue(() => work()); } catch (Exception) { return false; }
    }

    /// The names a --test second start may --open: the build's test stand-ins.
    public static IReadOnlyCollection<string> TestTargets()
        => Mantle.Generated.SurfaceCatalog.All.Select(d => d.Name).Where(SurfaceSet.IsTestOnly).ToArray();

    /// Make the set from the catalog and the AtStart surfaces (UI thread, once the host's bus, events and state exist).
    public void Start(SurfaceWires wires, string why)
    {
        if (Set != null) { _log.Line("surfaces: the host was started twice; the second start is ignored"); return; }
        var specs = Mantle.Generated.SurfaceCatalog.All
            .Select(d => new SurfaceSpec(d.Name, d.AtStart, () => new Slot(d, wires, _q, _test)))
            .ToArray();
        Set = new SurfaceSet(specs, new SurfaceSetOptions { Test = _test, TestSurfaces = _testSurfaces, Log = _log.Line });
        Set.Start(why);
    }

    /// Post <paramref name="work"/> on the set to the UI thread and finish when it has run (for the shutdown and the
    /// rest, which run on the host thread). A gone UI thread finishes at once with the reason.
    public Task<string> OnUiAsync(Func<SurfaceSet, string> work)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!PostUi(() => {
            try { tcs.TrySetResult(Set is { } s ? work(s) : "the surfaces were never set up"); }
            catch (Exception e) { tcs.TrySetResult("it threw: " + e.Message); }
        })) tcs.TrySetResult("the UI thread is gone");
        return tcs.Task;
    }

    /// One catalog entry made real: the ISurface and its context, from Create to Destroy.
    sealed class Slot : ISurfaceInstance
    {
        readonly Mantle.App.Shell.SurfaceDescriptor _d;
        readonly SurfaceWires _w;
        readonly DispatcherQueue _q;
        readonly bool _test;
        ISurface? _s;
        SurfaceLink? _link;

        public Slot(Mantle.App.Shell.SurfaceDescriptor d, SurfaceWires w, DispatcherQueue q, bool test) { _d = d; _w = w; _q = q; _test = test; }

        public void Create()
        {
            _link = new SurfaceLink(_d.Name, _w);
            _s = _d.Create();
            try { _s.Create(new SurfaceContext(_link, _q, _test)); }
            catch { _link.Dispose(); throw; }
        }

        public void Show(string why) => _s?.Show(why);
        public void Hide(string why) => _s?.Hide(why);
        public bool Shown => _s?.Shown ?? false;

        public void Destroy(string why)
        {
            try { _s?.Destroy(why); }
            finally { _link?.Dispose(); _s = null; _link = null; }
        }
    }
}

/// <summary>ISurfaceContext over the UI-free SurfaceLink: everything the frozen contract asks, for the surface's UI thread.</summary>
internal sealed class SurfaceContext : ISurfaceContext
{
    readonly SurfaceLink _link;

    public SurfaceContext(SurfaceLink link, DispatcherQueue q, bool test) { _link = link; Dispatcher = q; Test = test; }

    public string Name => _link.Name;
    public bool Test { get; }
    public DispatcherQueue Dispatcher { get; }
    public ILogSink Log => _link.Log;
    public ShellState State => _link.State;
    public IDisposable OnState(Action<ShellState> handler) => _link.OnState(handler);
    public ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null) => _link.CallAsync(name, args);
    public IDisposable On<T>(string ev, Action<T> handler) => _link.On(ev, handler);
}
