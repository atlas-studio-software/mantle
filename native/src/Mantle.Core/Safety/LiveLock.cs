using System;
using System.Threading;

namespace Mantle.Core.Safety;

/// <summary>
/// ONE SHELL AT A TIME, the half <see cref="TwoShells"/> cannot do: TwoShells makes a native --live run
/// refuse while Electron is live (it reads Electron's lock); this is the lock native HOLDS while it is the
/// live shell, so a second native live run (on another data root, which the single-instance claim does not
/// see) and, later, Electron can see it and refuse.
///
/// A named mutex per logon session, <see cref="MutexName"/> = <c>Local\Mantle.LiveShell</c>. The name is
/// the contract: Electron's main.js should check the same name before it goes live (through its sidecar,
/// a WaitOne(0) on <c>Mutex.OpenExisting</c>, released at once).
///
/// A named mutex is re-entrant on the thread that owns it and has thread affinity: take it on Main's
/// thread, keep the holder for the life of the process, dispose it on the same thread (or let the process
/// end: Windows releases it then, and the next taker hears "abandoned", which counts as taken, with a note).
/// </summary>
public static class LiveLock
{
    public const string MutexName = @"Local\Mantle.LiveShell";

    /// <summary>The lock, held. Dispose releases it (on the thread that took it).</summary>
    public sealed class Holder : IDisposable
    {
        readonly Mutex _m;
        bool _done;
        public string Name { get; }
        /// Set when the previous holder died holding it (an abandoned mutex): say it in the log.
        public string? Note { get; }

        internal Holder(Mutex m, string name, string? note) { _m = m; Name = name; Note = note; }

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            try { _m.ReleaseMutex(); } catch (ApplicationException) { /* not owned by this thread: the process end lets go */ } catch (ObjectDisposedException) { }
            _m.Dispose();
        }
    }

    /// <summary>
    /// Take the live lock without waiting. The holder, or null with <paramref name="refusal"/> in words.
    /// <paramref name="log"/> hears the abandoned case.
    /// </summary>
    public static Holder? TryTake(out string? refusal, Action<string>? log = null, string name = MutexName)
    {
        Mutex m;
        try { m = new Mutex(false, name); }
        catch (Exception e) {
            // fail closed: a lock that cannot be made or opened (access denied: another user's, an elevated one) is not ours
            refusal = $"the live-shell lock {name} could not be opened ({e.GetType().Name}: {e.Message}), so this Mantle did not go live";
            return null;
        }
        try {
            if (m.WaitOne(0)) { refusal = null; return new Holder(m, name, null); }
        } catch (AbandonedMutexException) {
            string note = $"the live-shell lock {name} was abandoned (the Mantle that held it ended without letting go: a crash or a kill); taken over";
            log?.Invoke(note);
            refusal = null;
            return new Holder(m, name, note);
        }
        m.Dispose();
        refusal = "another Mantle is already the live shell in this session (it holds " + name + "), so this one did not start: only one may own the taskbar, the keyboard hooks and the app bar. "
                + "Quit the other one first (MantleNative.exe --quit, or its tray menu), then start this one again.";
        return null;
    }
}
