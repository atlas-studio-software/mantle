using System;

namespace Mantle.Core.Commands;

// Host-to-surface events (Electron's broadcast / push / replay, and window.mantle.on(event) in a
// page). Payloads are typed C# values, not JSON: native surfaces are C#, and high-rate events (the
// window list, the clock, a meter) must not pay for serialisation. The WebView2 bridge of a later
// round serialises only the events it forwards. Contract only: native-core implements it.
//
// Threading: Publish, Replay and Subscribe are host-thread only and handlers run on the host thread.
// Surfaces subscribe through their surface context, which re-posts each payload to the surface's UI
// thread; that is why a payload must be immutable (a record, or an immutable collection).

/// <summary>Named, typed events with current-value replay.</summary>
public interface IEvents
{
    /// Send <paramref name="payload"/> to every subscriber of <paramref name="name"/> (Electron's broadcast).
    void Publish<T>(string name, T payload);

    /// <summary>
    /// Every <paramref name="name"/> event from now on. If a replay source is registered for the name,
    /// the handler gets its current value at once, so a surface never shows an empty first frame.
    /// </summary>
    /// <remarks>A subscriber whose <typeparamref name="T"/> does not match what is published is a
    /// programming error: it is logged with both type names and the handler is not called (never a
    /// silent drop).</remarks>
    IDisposable Subscribe<T>(string name, Action<T> handler);

    /// Register where the current value of <paramref name="name"/> comes from, for replay on subscribe
    /// (Electron's ctx.replay). One source per name; a second is refused and named.
    IDisposable Replay<T>(string name, Func<T> current);
}
