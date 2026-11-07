using System;
using System.Collections.Generic;
using System.Threading;
using Mantle.Core.Logging;

namespace Mantle.Core.Data;

/// <summary>
/// <see cref="IStores"/>: one <see cref="JsonStore"/> per file under the data root for the life of the process,
/// all writing through one <see cref="SafeFile"/> (one read-only gate). <see cref="CheckNow"/> (and the poll
/// <see cref="StartWatching"/> starts) re-reads a file another program changed and tells its listeners; a
/// store's own writes never count as a change.
/// </summary>
public sealed class Stores : IStores, IDisposable
{
    readonly string _root;
    readonly SafeFile _files;
    readonly ILogSink _log;
    readonly Dictionary<string, JsonStore> _open = new(StringComparer.OrdinalIgnoreCase);
    Timer? _poll;

    public Stores(string dataRoot, SafeFile files, ILogSink log)
    {
        _root = dataRoot;
        _files = files;
        _log = log;
    }

    /// The stores for this run: the data root of <paramref name="paths"/>, behind <paramref name="gate"/>.
    public static Stores For(DataPaths paths, WriteGate gate, ILogSink log) => new(paths.DataRoot, new SafeFile(gate), log);

    public SafeFile Files => _files;

    public IStore Open(string relativePath)
    {
        var spec = DataFileSpecs.For(relativePath)
            ?? throw new ArgumentException($"\"{relativePath}\" is not a settings file Electron Mantle writes (docs/data-files.md (a)); add it to DataFileSpecs with its exact format first", nameof(relativePath));
        lock (_open) {
            if (!_open.TryGetValue(spec.Name, out var s)) _open[spec.Name] = s = new JsonStore(_root, spec, _files, _log);
            return s;
        }
    }

    /// Re-read every open file whose stamp moved since it was last read or written, and tell its listeners.
    /// <param name="post">runs each listener call on the host thread (null = right here)</param>
    public void CheckNow(Action<Action>? post = null)
    {
        JsonStore[] all;
        lock (_open) all = new List<JsonStore>(_open.Values).ToArray();
        foreach (var s in all)
            if (s.DiskStamp != s.LoadedStamp && s.Load()) { if (post != null) post(s.Tell); else s.Tell(); }
    }

    /// Poll every <paramref name="every"/> (default 1 s). Electron writes by rename, which a folder watch can
    /// miss across sleep; a stamp poll of a handful of small files costs nothing.
    public void StartWatching(TimeSpan? every = null, Action<Action>? post = null)
    {
        var t = every ?? TimeSpan.FromSeconds(1);
        _poll?.Dispose();
        _poll = new Timer(_ => { try { CheckNow(post); } catch (Exception e) { _log.Line("stores: a check failed: " + e.Message); } }, null, t, t);
    }

    public void Dispose()
    {
        _poll?.Dispose();
        _poll = null;
    }
}
