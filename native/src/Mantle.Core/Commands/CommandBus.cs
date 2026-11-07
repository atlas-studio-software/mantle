using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Commands;

/// <summary>
/// The host's command bus (ICommandBus): Electron's COMMANDS table and its ipcMain 'mantle:call'
/// handler. Never throws at a caller: an unknown name, a missing name, a handler that throws or
/// answers null all come back as <see cref="CommandResult.Fail"/> in plain words (LOG-011), and every
/// answer goes through the call log (LOG-006). Host thread only.
/// </summary>
public sealed class CommandBus : ICommandBus
{
    readonly Dictionary<string, CommandRegistration> _commands = new(StringComparer.Ordinal);
    readonly CallLog? _calls;
    readonly Func<string, string>? _plain;

    /// <param name="calls">the call log (null: nothing is logged)</param>
    /// <param name="plain">turns a thrown error's message into plain words (lib/plain-error.js); identity when null</param>
    public CommandBus(CallLog? calls = null, Func<string, string>? plain = null) { _calls = calls; _plain = plain; }

    public IReadOnlyCollection<string> Names => _commands.Keys.ToArray();
    public bool Has(string name) => _commands.ContainsKey(name);

    public IDisposable Register(CommandRegistration command)
    {
        if (command == null) throw new ArgumentNullException(nameof(command));
        if (string.IsNullOrWhiteSpace(command.Name)) throw new ArgumentException($"{command.Owner} tried to register a command with no name");
        if (_commands.TryGetValue(command.Name, out var taken))
            throw new InvalidOperationException($"the command \"{command.Name}\" is already registered by {taken.Owner}, so {command.Owner} cannot register it too");
        _commands[command.Name] = command;
        return new Unregister(this, command);
    }

    public async ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null, string caller = "host", CancellationToken cancel = default)
    {
        if (string.IsNullOrEmpty(name)) return CommandResult.Fail("Mantle was asked for a command with no name");
        if (!_commands.TryGetValue(name, out var reg)) {
            var r = CommandResult.Fail($"Mantle has no command called \"{name}\"");
            _calls?.Result(name, args, r);
            return r;
        }
        JsonElement detached;
        try { detached = Detach(args); }
        catch (Exception e) {
            var r = CommandResult.Fail("This page asked Mantle for something in a form it cannot send, so nothing was done. The details are in Mantle's log.");
            _calls?.Threw(name, null, "the arguments could not be read: " + e.Message);
            return r;
        }
        CommandResult result;
        try {
            result = await reg.Handler(new CommandCall(name, detached, caller, cancel)) ?? CommandResult.Fail($"\"{name}\" answered nothing, which is a fault in Mantle");
        } catch (OperationCanceledException) when (cancel.IsCancellationRequested) {
            result = CommandResult.Fail($"\"{name}\" was cancelled");
        } catch (Exception e) {
            string words = _plain != null ? _plain(e.Message) : e.Message;
            _calls?.Threw(name, args, e.GetType().Name + ": " + e.Message);
            return CommandResult.Fail(string.IsNullOrWhiteSpace(words) ? "command failed: " + e.GetType().Name : words);
        }
        _calls?.Result(name, args, result);
        return result;
    }

    /// The arguments as a detached object (an empty one for none).
    static JsonElement Detach(JsonObject? args)
    {
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf)) {
            if (args == null) { w.WriteStartObject(); w.WriteEndObject(); }
            else args.WriteTo(w);
        }
        using var doc = JsonDocument.Parse(buf.ToArray());
        return doc.RootElement.Clone();
    }

    sealed class Unregister : IDisposable
    {
        CommandBus? _bus;
        readonly CommandRegistration _reg;
        public Unregister(CommandBus bus, CommandRegistration reg) { _bus = bus; _reg = reg; }
        public void Dispose()
        {
            var b = Interlocked.Exchange(ref _bus, null);
            if (b != null && b._commands.TryGetValue(_reg.Name, out var now) && ReferenceEquals(now, _reg)) b._commands.Remove(_reg.Name);
        }
    }
}

/// <summary>
/// lib/call-log.js createCallLog: a failed call is written ONCE per command + arguments; the same
/// failure again is only counted; a different reason is written with how often the previous one
/// repeated; the first success after a failure is written as a recovery. String arguments are shown as
/// <c>&lt;N chars&gt;</c> except Mantle's own words (mode, kind, ...), so a Wi-Fi name or a path never
/// reaches the log (LOG-006).
/// </summary>
public sealed class CallLog
{
    public const int MaxArgs = 120;
    public static readonly HashSet<string> SafeArgKeys = new(StringComparer.Ordinal) { "mode", "kind", "action", "lane", "corner", "combo", "side", "state", "level", "what", "via", "verb", "url" };

    readonly Action<string> _write;
    readonly Func<string, bool> _skip;
    readonly int _maxKeys;
    readonly LinkedList<string> _order = new();
    readonly Dictionary<string, (string Reason, int Repeats, LinkedListNode<string> Node)> _open = new(StringComparer.Ordinal);

    public CallLog(Action<string> write, Func<string, bool>? skip = null, int maxKeys = 200)
    {
        _write = write; _skip = skip ?? (_ => false); _maxKeys = maxKeys;
    }

    public int OpenCount => _open.Count;

    /// A command's answer: a failure is recorded, a success closes an open failure.
    public void Result(string name, JsonObject? args, CommandResult r)
    {
        if (!r.Ok) Failed(name, args, r.Error ?? "ok:false, no reason given", false);
        else Succeeded(name, args);
    }

    /// A handler threw: always written, even for a skipped name.
    public void Threw(string name, JsonObject? args, string message) => Failed(name, args, "threw: " + message, true);

    void Failed(string name, JsonObject? args, string reason, bool always)
    {
        if (!always && _skip(name)) return;
        string key = KeyOf(name, args);
        if (_open.TryGetValue(key, out var prev)) {
            if (prev.Reason == reason) { _open[key] = (prev.Reason, prev.Repeats + 1, prev.Node); return; }
            _write($"mantle: {ShownOf(name, args)} -> {reason} (before this, \"{Short(prev.Reason, 80)}\" failed {prev.Repeats + 1} time(s) in a row)");
            _order.Remove(prev.Node);
            _open.Remove(key);
        } else {
            _write($"mantle: {ShownOf(name, args)} -> {reason}");
        }
        _open[key] = (reason, 0, _order.AddLast(key));
        while (_open.Count > _maxKeys) {
            var first = _order.First!;
            _order.RemoveFirst();
            _open.Remove(first.Value);
        }
    }

    void Succeeded(string name, JsonObject? args)
    {
        if (_open.Count == 0) return;
        string key = KeyOf(name, args);
        if (!_open.Remove(key, out var prev)) return;
        _order.Remove(prev.Node);
        _write($"mantle: {ShownOf(name, args)} recovered after {prev.Repeats + 1} failure(s) in a row (the last: {Short(prev.Reason, 160)})");
    }

    static string KeyOf(string name, JsonObject? args)
        => name + (args is { Count: > 0 } ? " " + Cut(args.ToJsonString(), MaxArgs) : "");

    static string ShownOf(string name, JsonObject? args)
        => name + (args is { Count: > 0 } ? " " + Cut(Shown(args), MaxArgs) : "");

    /// JSON.stringify with a replacer: every string value under a key that is not one of Mantle's own words becomes &lt;N chars&gt;.
    public static string Shown(JsonNode? node)
    {
        var buf = new MemoryStream();
        using (var w = new Utf8JsonWriter(buf)) Write(w, node, null);
        return Encoding.UTF8.GetString(buf.ToArray());
    }

    static void Write(Utf8JsonWriter w, JsonNode? node, string? key)
    {
        switch (node) {
            case null: w.WriteNullValue(); break;
            case JsonObject o:
                w.WriteStartObject();
                foreach (var kv in o) { w.WritePropertyName(kv.Key); Write(w, kv.Value, kv.Key); }
                w.WriteEndObject();
                break;
            case JsonArray a:
                w.WriteStartArray();
                for (int i = 0; i < a.Count; i++) Write(w, a[i], i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                w.WriteEndArray();
                break;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String && key != null && !SafeArgKeys.Contains(key):
                w.WriteStringValue($"<{v.GetValue<string>().Length} chars>");
                break;
            default: node.WriteTo(w); break;
        }
    }

    static string Cut(string s, int n) => s.Length > n ? s[..n] : s;
    static string Short(string s, int n) => s.Length > n ? s[..(n - 1)] + "…" : s;
}

/// <summary>
/// lib/call-log.js createSetLog: <c>qs.set</c> (the user changing something) is ALWAYS a line with its
/// result and latency, except a dragged slider's stream: the first set at once, the rest summed in one
/// line when the drag stops (LOG-007). Host thread only.
/// </summary>
public sealed class SetLog
{
    readonly Action<string> _write;
    readonly Func<TimeSpan, Action, IDisposable> _delay;
    readonly int _quietMs;
    readonly HashSet<string> _sliders;
    readonly Dictionary<string, Burst> _bursts = new(StringComparer.Ordinal);

    sealed class Burst { public int N; public double MinMs = double.PositiveInfinity, MaxMs; public string LastValue = "(none)", LastRead = "(none)"; public IDisposable? Timer; }

    public SetLog(Action<string> write, Func<TimeSpan, Action, IDisposable> delay, int quietMs = 750, IEnumerable<string>? sliders = null)
    {
        _write = write; _delay = delay; _quietMs = quietMs;
        _sliders = new HashSet<string>(sliders ?? new[] { "volume", "brightness" }, StringComparer.Ordinal);
    }

    /// <param name="value">the asked value, already as JS would print it (fmt: strings quoted)</param>
    /// <param name="read">the value read back on success (fmt), else null</param>
    public void Record(string part, string value, bool ok, string? read, string? reason, double ms)
    {
        if (!_sliders.Contains(part) || !ok) {
            Flush(part);
            _write(Line(part, value, ok, read, reason, ms));
            return;
        }
        if (!_bursts.TryGetValue(part, out var b)) {
            _write(Line(part, value, ok, read, reason, ms));
            b = new Burst();
            _bursts[part] = b;
        } else {
            b.N++;
            b.MinMs = Math.Min(b.MinMs, ms);
            b.MaxMs = Math.Max(b.MaxMs, ms);
            b.LastValue = value;
            b.LastRead = read ?? "(none)";
        }
        b.Timer?.Dispose();
        b.Timer = _delay(TimeSpan.FromMilliseconds(_quietMs), () => { b.Timer = null; Flush(part); });
    }

    public void FlushAll() { foreach (var p in _bursts.Keys.ToArray()) Flush(p); }

    void Flush(string part)
    {
        if (!_bursts.Remove(part, out var b)) return;
        b.Timer?.Dispose();
        if (b.N > 0)
            _write($"mantle: qs.set {part}: {b.N} more while it was dragged, the last {b.LastValue} (reads {b.LastRead}), all ok, {Ms(b.MinMs)}-{Ms(b.MaxMs)} ms each");
    }

    static string Line(string part, string value, bool ok, string? read, string? reason, double ms)
        => $"mantle: qs.set {part}={value} -> {(ok ? "ok, reads " + (read ?? "(none)") : "NOT DONE: " + (reason ?? "no reason given"))} ({Ms(ms)} ms)";

    static string Ms(double ms) => ms.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
