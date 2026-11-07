using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Mantle.Core.Logging;

namespace Mantle.Core.Data;

/// Which of the watched files changed.
public enum DataFile { Theme, LocalLook, SharedLook, Features, Pins }

public sealed record DataChange(DataFile File, string Description);

/// <summary>
/// Electron Mantle's data, READ ONLY: the files the bar needs (DATA.md has the full inventory),
/// read at start and again whenever Electron changes one.
///
/// There is no method here that writes, moves or deletes anything, by design: Electron owns these
/// files (it writes them whole as tmp + rename, and moves a damaged one aside itself). Every read
/// that does not give a clean value is logged with its reason, and the defaults are used.
///
/// Watching: a FileSystemWatcher on each folder that exists (the data root, its features folder,
/// the shared look folder) for a quick reaction, plus a slow poll of the five files' stamps that
/// also attaches watchers to folders that appear later and covers any event a watcher drops.
/// Changed is raised on a thread-pool thread, once per file whose stamp moved.
/// </summary>
public sealed class MantleData : IDisposable
{
    readonly ILogSink _log;
    readonly object _gate = new();
    readonly Dictionary<DataFile, string> _files;
    readonly Dictionary<DataFile, string> _stamps = new();
    readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    Timer? _poll;
    Timer? _debounce;
    bool _disposed;

    public DataPaths Paths { get; }
    public ReadResult<ThemeSettings> Theme { get; private set; }
    public ReadResult<LocalLook> LocalLook { get; private set; }
    public ReadResult<SharedLook> SharedLook { get; private set; }
    public ReadResult<FeatureSwitches> Features { get; private set; }
    public ReadResult<PinOverlay> Pins { get; private set; }

    /// One event per file whose stamp moved (written, replaced, created or removed).
    public event Action<DataChange>? Changed;

    public MantleData(DataPaths paths, ILogSink log)
    {
        Paths = paths;
        _log = log;
        _files = new Dictionary<DataFile, string> {
            [DataFile.Theme] = Path.Combine(paths.DataRoot, Readers.ThemeFile),
            [DataFile.LocalLook] = Path.Combine(paths.DataRoot, Readers.LocalLookFile),
            [DataFile.Features] = Path.Combine(paths.DataRoot, Readers.FeaturesFile),
            [DataFile.Pins] = Path.Combine(paths.DataRoot, Readers.PinsFile),
            [DataFile.SharedLook] = Path.Combine(paths.LookSyncDir, Readers.SharedLookFile),
        };
        string none = "not read yet";
        Theme = Readers.Result(ThemeSettings.Default, ReadState.Missing, none, null, Readers.ThemeFile, "");
        LocalLook = Readers.Result(Data.LocalLook.Default, ReadState.Missing, none, null, Readers.LocalLookFile, "");
        SharedLook = Readers.Result(Data.SharedLook.None, ReadState.Missing, none, null, Readers.SharedLookFile, "");
        Features = Readers.Result(FeatureSwitches.Default, ReadState.Missing, none, null, Readers.FeaturesFile, "");
        Pins = Readers.Result(PinOverlay.Empty, ReadState.Missing, none, null, Readers.PinsFile, "");
    }

    public string PathOf(DataFile f) => _files[f];

    /// Read every file now (and log what each gave).
    public void Load()
    {
        lock (_gate) foreach (var f in _files.Keys) Reload(f);
    }

    /// The glass the bar should show: the shared look's when sync is on and look.json carries a
    /// usable one, else Mantle's own copy (look-sync.json), else the seed.
    public GlassSettings EffectiveGlass
    {
        get {
            lock (_gate) {
                if (LocalLook.Value.LookSync && SharedLook.State == ReadState.Ok && SharedLook.Value.Glass != null) return SharedLook.Value.Glass;
                return LocalLook.Value.Glass;
            }
        }
    }

    void Reload(DataFile f)
    {
        string path = _files[f];
        string stamp = ReadOnlyFile.StampOf(path);
        _stamps[f] = stamp;
        string name = f == DataFile.SharedLook ? Path.GetFileName(Paths.LookSyncDir) + "\\" + Readers.SharedLookFile : (f == DataFile.Pins ? Readers.PinsFile : Path.GetFileName(path));
        long max = f == DataFile.Theme ? Readers.ThemeMaxBytes : Readers.OtherMaxBytes;
        string? text = ReadOnlyFile.TryRead(path, max, out var st, out var why);
        string words;
        switch (f) {
            case DataFile.Theme:
                Theme = text != null ? Readers.ParseTheme(text, name, stamp) : Readers.Result(ThemeSettings.Default, st, why, null, name, stamp);
                words = "mode " + ThemeSettings.Word(Theme.Value.Mode);
                _log.Line("data: " + Theme.Describe(words));
                break;
            case DataFile.LocalLook:
                LocalLook = text != null ? Readers.ParseLocalLook(text, name, stamp) : Readers.Result(Data.LocalLook.Default, st, why, null, name, stamp);
                _log.Line("data: " + LocalLook.Describe(LocalLook.Value.ToString()));
                break;
            case DataFile.SharedLook:
                SharedLook = text != null ? Readers.ParseSharedLook(text, name, stamp) : Readers.Result(Data.SharedLook.None, st, why, null, name, stamp);
                _log.Line("data: " + SharedLook.Describe(SharedLook.State == ReadState.Ok ? SharedLook.Value.ToString() : null));
                break;
            case DataFile.Features:
                Features = text != null ? Readers.ParseFeatures(text, name, stamp) : Readers.Result(FeatureSwitches.Default, st, why, null, name, stamp);
                _log.Line("data: " + Features.Describe(Features.Value.ToString()));
                break;
            case DataFile.Pins:
                Pins = text != null ? Readers.ParsePins(text, name, stamp) : Readers.Result(PinOverlay.Empty, st, why, null, name, stamp);
                _log.Line("data: " + Pins.Describe(Pins.Value.ToString()));
                break;
        }
    }

    /// Start watching. poll = how often the stamps are compared (default 2 s).
    public void StartWatching(TimeSpan? poll = null)
    {
        lock (_gate) {
            if (_disposed || _poll != null) return;
            EnsureWatchers();
            var every = poll ?? TimeSpan.FromSeconds(2);
            _debounce = new Timer(_ => CheckNow(), null, Timeout.Infinite, Timeout.Infinite);
            _poll = new Timer(_ => { lock (_gate) { if (!_disposed) EnsureWatchers(); } CheckNow(); }, null, every, every);
            _log.Line($"data: watching {_watchers.Count} folder(s) for Electron's changes, and checking every {every.TotalSeconds:0.#} s");
        }
    }

    void EnsureWatchers()
    {
        foreach (var dir in new[] { Paths.DataRoot, Path.Combine(Paths.DataRoot, "features"), Paths.LookSyncDir }) {
            if (_watchers.ContainsKey(dir) || !Directory.Exists(dir)) continue;
            try {
                var w = new FileSystemWatcher(dir) {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    InternalBufferSize = 16 * 1024,
                };
                FileSystemEventHandler on = (_, e) => Poke(e.Name);
                w.Changed += on; w.Created += on; w.Deleted += on;
                w.Renamed += (_, e) => { Poke(e.OldName); Poke(e.Name); };
                string d = dir;
                w.Error += (_, e) => {
                    _log.Line($"data: the watcher on {Path.GetFileName(d)} stopped ({e.GetException().Message}); the poll carries on and it is re-attached");
                    lock (_gate) { if (_watchers.Remove(d, out var dead)) dead.Dispose(); }
                    Poke(null);
                };
                w.EnableRaisingEvents = true;
                _watchers[dir] = w;
            } catch (Exception e) {
                _log.Line($"data: {Path.GetFileName(dir)} cannot be watched ({e.GetType().Name}: {e.Message}); the poll still sees its changes");
            }
        }
    }

    static readonly HashSet<string> Interesting = new(StringComparer.OrdinalIgnoreCase) {
        Readers.ThemeFile, Readers.LocalLookFile, Readers.FeaturesFile, "jumplist.json", Readers.SharedLookFile,
    };

    void Poke(string? name)
    {
        if (name != null && !Interesting.Contains(Path.GetFileName(name))) return;   // tmp files, logs, other files
        lock (_gate) { if (!_disposed) _debounce?.Change(120, Timeout.Infinite); }
    }

    /// Compare every file's stamp with the last read, re-read the ones that moved, raise Changed.
    public void CheckNow()
    {
        var changes = new List<DataChange>();
        lock (_gate) {
            if (_disposed) return;
            foreach (var f in _files.Keys) {
                string now = ReadOnlyFile.StampOf(_files[f]);
                if (_stamps.TryGetValue(f, out var was) && was == now) continue;
                Reload(f);
                changes.Add(new DataChange(f, f switch {
                    DataFile.Theme => Theme.Describe(),
                    DataFile.LocalLook => LocalLook.Describe(),
                    DataFile.SharedLook => SharedLook.Describe(),
                    DataFile.Features => Features.Describe(),
                    _ => Pins.Describe(),
                }));
            }
        }
        foreach (var c in changes) {
            try { Changed?.Invoke(c); } catch (Exception e) { _log.Line($"data: a change handler failed on {c.File} ({e.GetType().Name}: {e.Message})"); }
        }
    }

    public void Dispose()
    {
        lock (_gate) {
            if (_disposed) return;
            _disposed = true;
            _poll?.Dispose();
            _debounce?.Dispose();
            foreach (var w in _watchers.Values) w.Dispose();
            _watchers.Clear();
        }
    }
}
