using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Logging;

namespace Mantle.Core.Data;

/// <summary>
/// One JSON object file in the profile (<see cref="IStore"/>), written exactly as Electron writes it
/// (<see cref="DataFileSpec"/>, <see cref="JsJson"/>), through <see cref="SafeFile"/> and so through the read-only gate.
///
/// Load: missing = <c>{}</c>; a file that is not a file, over Electron's own limit, not JSON or not an object is
/// unusable: the store starts from <c>{}</c> and, before its first write, moves the file aside as
/// <c>&lt;name&gt;.unreadable</c> (or <c>.unreadable-&lt;ms&gt;</c> when that exists), never over it. When the move is
/// refused, nothing is saved over it. A file over native's safety ceiling where Electron has no limit is left
/// alone and every write refused (Electron itself would still read it).
/// Writes are synchronous and serialised per file; the returned task is already complete.
/// </summary>
public sealed class JsonStore : IStore
{
    readonly object _lock = new();
    readonly string _path;
    readonly DataFileSpec _spec;
    readonly SafeFile _files;
    readonly ILogSink _log;
    readonly Func<long> _nowMs;
    readonly List<Action<IStore>> _handlers = new();
    JsonObject _doc = new();
    bool _needsAside;
    string? _noWrite;
    string _stamp = "";

    public string Name { get; }
    public ReadState State { get; private set; }
    public string? Reason { get; private set; }
    public string FullPath => _path;
    public DataFileSpec Spec => _spec;

    public JsonStore(string root, DataFileSpec spec, SafeFile files, ILogSink log, Func<long>? nowMs = null)
    {
        _spec = spec;
        Name = spec.Name;
        _path = Path.Combine(root, spec.Name);
        _files = files;
        _log = log;
        _nowMs = nowMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Load();
    }

    /// (Re)read the file. Answers true when what is on disk differs from what was loaded before.
    internal bool Load()
    {
        lock (_lock) {
            string stamp = ReadOnlyFile.StampOf(_path);
            bool moved = stamp != _stamp;
            _stamp = stamp;
            _needsAside = false;
            _noWrite = null;
            _problem = null;
            _keptAs = null;
            Reason = null;
            JsonObject doc = new();
            string? why = null;
            ReadState state = ReadState.Ok;
            try {
                var fi = new FileInfo(_path);
                if (!fi.Exists) {
                    if (Directory.Exists(_path)) { why = "is not a file"; state = ReadState.Corrupt; }
                    else state = ReadState.Missing;
                } else if (_spec.AsideOverBytes > 0 && fi.Length > _spec.AsideOverBytes) {
                    why = _spec.AsideOverBytes >= 1048576
                        ? $"is {(fi.Length / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} MB, over the {_spec.AsideOverBytes / 1048576} MB a settings file may be"
                        : $"is larger than {_spec.AsideOverBytes / 1024} KB";
                    state = ReadState.TooLarge;
                } else if (fi.Length > DataFileSpec.SafetyMaxBytes) {
                    state = ReadState.TooLarge;
                    _noWrite = $"{Name} is {fi.Length / 1048576} MB, more than native Mantle reads, so it is left as it is and nothing is saved over it";
                    Reason = _noWrite;
                } else {
                    byte[] bytes;
                    using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                        bytes = new byte[fs.Length];
                        fs.ReadExactly(bytes);
                    }
                    JsonNode? v;
                    try { v = JsJson.Parse(JsJson.DecodeUtf8(bytes)); } catch (JsonException) { v = null; why = "is not valid JSON"; }
                    catch (InvalidOperationException) { v = null; why = "is not valid JSON"; }
                    if (why == null) {
                        if (v is JsonObject o) doc = o;
                        else why = "does not hold settings (not a JSON object)";
                    }
                    if (why != null) state = ReadState.Corrupt;
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                why = "could not be read (" + (e is UnauthorizedAccessException ? "EPERM" : "EIO") + ")";
                state = ReadState.Unreadable;
            }
            State = state;
            _doc = doc;
            if (why != null) Unusable(why);
            return moved;
        }
    }

    /// <summary>
    /// The file's content is unusable for its owner (for example theme.json with a mode that is not a mode):
    /// start from <c>{}</c> and move the file aside now when writes are allowed, else before the first write.
    /// Answers the aside name, or null when it stays in place for now.
    /// </summary>
    public string? MoveAsideNow(string why)
    {
        lock (_lock) {
            _doc = new JsonObject();
            State = ReadState.Corrupt;
            Unusable(why);
            return _keptAs;
        }
    }

    string? _keptAs;

    /// Why the file on disk was not used ("is not valid JSON"), or null when it was.
    public string? Problem { get { lock (_lock) return _problem; } }
    string? _problem;

    void Unusable(string why)
    {
        _problem = why;
        _needsAside = true;
        _keptAs = null;
        _log.Line($"{Name}: the saved settings were not used: {Name} {why}");   // the name only, never the path
        Reason = $"the saved settings were not used: {Name} {why}; starting from defaults";
        if (_files.Gate.Refusal(_path) == null) AsideNow(why);
        else Reason = $"the saved settings were not used: {Name} {why}; it is left in place, starting from defaults";
    }

    /// The .unreadable rule: rename, never over an existing aside copy. False (and nothing saved over it) when the move fails.
    bool AsideNow(string? why = null)
    {
        if (!File.Exists(_path) && !Directory.Exists(_path)) { _needsAside = false; return true; }
        string aside = File.Exists(_path + ".unreadable") || Directory.Exists(_path + ".unreadable")
            ? _path + ".unreadable-" + _nowMs() : _path + ".unreadable";
        string? err = _files.MoveAside(_path, aside);
        if (err != null) {
            _noWrite = $"{Name} could not be used and could not be moved aside ({err}), so nothing is saved over it";
            Reason = _noWrite;
            _log.Line($"{Name}: {_noWrite}");
            return false;
        }
        _needsAside = false;
        _keptAs = Path.GetFileName(aside);
        _stamp = ReadOnlyFile.StampOf(_path);
        Reason = $"the saved settings were not used: {Name} {why ?? "could not be used"}; kept as {_keptAs}, starting from defaults";
        return true;
    }

    /// The aside copy made for the last unusable file, if any (theme's note names it).
    public string? KeptAs { get { lock (_lock) return _keptAs; } }

    internal string DiskStamp => ReadOnlyFile.StampOf(_path);
    internal string LoadedStamp { get { lock (_lock) return _stamp; } }

    public JsonElement Snapshot()
    {
        lock (_lock) return Element(_doc);
    }

    static JsonElement Element(JsonNode n)
    {
        using var d = JsonDocument.Parse(JsJson.Stringify(n, 0));
        return d.RootElement.Clone();
    }

    public bool TryGet(string key, out JsonElement value)
    {
        lock (_lock) {
            if (_doc.TryGetPropertyValue(key, out var n)) {
                value = n == null ? NullElement : Element(n);
                return true;
            }
            value = default;
            return false;
        }
    }

    static readonly JsonElement NullElement = JsonDocument.Parse("null").RootElement.Clone();

    public ValueTask<StoreWrite> SetAsync(string key, JsonNode? value)
    {
        if (key == null) throw new ArgumentNullException(nameof(key));
        var copy = JsJson.Clone(value);
        return Change(d => d[key] = copy);
    }

    public ValueTask<StoreWrite> RemoveAsync(string key)
    {
        lock (_lock) if (!_doc.ContainsKey(key)) return ValueTask.FromResult(StoreWrite.Done);
        return Change(d => d.Remove(key));
    }

    public ValueTask<StoreWrite> UpdateAsync(Action<JsonObject> edit)
    {
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        return Change(edit);
    }

    ValueTask<StoreWrite> Change(Action<JsonObject> edit)
    {
        lock (_lock) {
            var next = (JsonObject)JsJson.Clone(_doc)!;
            edit(next);
            next = (JsonObject)JsJson.Clone(next)!;     // JSON-only, never shared by reference (lib/features.js)
            _doc = next;                                // the in-memory document is the truth, saved or not
            return ValueTask.FromResult(Save());
        }
    }

    StoreWrite Save()
    {
        if (_spec.NeverWritten) return StoreWrite.Failed($"{Name} is the user's own hand-written file; Mantle reads it and never writes it");
        if (_noWrite != null) return StoreWrite.Failed(_noWrite);
        string? refused = _files.Gate.Refusal(_path);
        if (refused != null) return StoreWrite.Failed(refused);
        if (_needsAside && !AsideNow()) return StoreWrite.Failed(_noWrite ?? Reason ?? (Name + " could not be moved aside"));
        string tmp = _spec.PidTmp ? $"{_path}.{Environment.ProcessId}.tmp" : _path + ".tmp";
        string? fail = _files.WriteAtomic(_path, JsJson.ToFileBytes(_doc, _spec.Indent, _spec.TrailingNewline), tmp);
        if (fail != null) return StoreWrite.Failed($"{Name} could not be saved ({fail})");
        _stamp = ReadOnlyFile.StampOf(_path);
        if (State != ReadState.Ok) State = ReadState.Ok;
        return StoreWrite.Done;
    }

    public IDisposable OnChanged(Action<IStore> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        lock (_lock) _handlers.Add(handler);
        return new Off(() => { lock (_lock) _handlers.Remove(handler); });
    }

    internal void Tell()
    {
        Action<IStore>[] hs;
        lock (_lock) hs = _handlers.ToArray();
        foreach (var h in hs) {
            try { h(this); } catch (Exception e) { _log.Line($"{Name}: a change listener threw: {e.Message}"); }
        }
    }

    sealed class Off : IDisposable
    {
        Action? _a;
        public Off(Action a) => _a = a;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }
}
