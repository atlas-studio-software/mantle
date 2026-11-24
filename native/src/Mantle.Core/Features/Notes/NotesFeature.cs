using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Notes;

/// <summary>
/// lane r10-notes: [Feature("notes")] (lib/features/notes.js), the quick notes pad's service. Win+Alt+N toggles the pad:
/// closed opens it, open closes it (with refocus). The host hands the chord back to Windows while the feature is off,
/// unhealthy or resting, so then Windows' own Win+Alt+N runs. The kill switch is never seen here (the sidecar and the
/// host own it).
///
/// The text lives in <see cref="Pad"/> (the surface edits it in-process: no note text ever goes through a command, a
/// broadcast argument or a log line). Autosave: 400 ms after typing stops, at most 3 s into a burst, at once on blur,
/// on close, on stop (quit, session end) and on rest; a save that cannot be written keeps the text in the pad, says why,
/// is tried again on the next keystroke and every 5 s; at quit a last-ditch rescue copy is tried. A rest carries unsaved
/// notes in module scope and saves them at wake (NOTE-091).
///
/// Every write goes through <see cref="SafeFile"/>: without a configured gate (<see cref="Configure"/>) nothing is saved
/// at all (fail closed) and the pad says so. The live folder is the one Electron Mantle writes; today's gate refuses it,
/// so a native run reads the user's notes but never writes them.
/// </summary>
[Feature("notes", "Quick notes (Win+Alt+N)", true, Chords = new[] { "Win+Alt+N" }, Rest = true)]
public sealed class NotesFeature : IFeature
{
    public const string Chord = "Win+Alt+N";
    public const string StateEvent = "notes.state";
    public const string OpenEvent = "notes.open";
    public const string NewEvent = "notes.new";
    public const string Healthy = "Win+Alt+N or the notes icon on the top bar opens the pad";
    public const string OffWords = "quick notes is switched off in the tray";
    public const string StoppedWords = "quick notes is stopped";
    public const string NoGate = "quick notes was given no write gate, so nothing typed here is saved";
    public const int BlurGraceMs = 400;

    /// <summary>What the feature is given (tests: a temp folder, a scratch gate, a manual clock).</summary>
    public sealed record Host(string? Dir = null, SafeFile? Safe = null, HostClock? Clock = null, Func<DateTime>? LocalNow = null,
        Func<long>? Now = null, string? RescueDir = null, Action<int>? Sleep = null, string? UserProfile = null, string? DataRoot = null,
        bool Surface = false);

    /// <summary>Until a pad surface is attached (<see cref="Host.Surface"/>), the feature is unhealthy with these words, so the
    /// host hands Win+Alt+N back to Windows instead of swallowing a chord that would show nothing.</summary>
    public const string NoSurface = "the notes pad is not drawn in native Mantle yet, so Win+Alt+N stays Windows' own";

    static Host? _configured;
    public static void Configure(Host h) => _configured = h;

    /// Unsaved notes a rest left behind (module scope: they outlive the instance; NOTE-091).
    static Carry? _carry;
    sealed record Carry(string[] Texts, string Reason);

    /// The live instance, for the pad surface.
    public static NotesFeature? Current { get; private set; }

    /// Tests: forget the module scope (the rest carry and the live instance) between runs.
    internal static void ResetModule() { _carry = null; Current = null; _configured = null; }

    /// Tests: is a rest carry waiting?
    internal static bool Carrying => _carry != null;

    readonly Host? _given;
    public NotesFeature() { }
    public NotesFeature(Host h) { _given = h; }

    IFeatureContext _ctx = null!;
    Host _h = null!;
    HostClock _clock = null!;
    NotesStore _store = null!;
    SafeFile _safe = null!;
    string? _rescueDir;
    readonly NotesAutosave _auto = new();
    IDisposable? _timer;
    bool _stopped, _open;
    long _blurClosedAt = long.MinValue / 2;
    string? _refocus;
    int _savedActive;
    JsonObject _saved = new() { ["ok"] = true, ["at"] = null, ["reason"] = null, ["rev"] = 0 };
    JsonObject? _outside;
    readonly HashSet<string> _loggedReasons = new(StringComparer.Ordinal);
    IReadOnlyList<string> _rescued = Array.Empty<string>();

    public NotesPad Pad { get; } = new();
    public bool IsOpen => _open;
    public string? RefocusTarget => _refocus;
    /// The open pad's notice and footer read these.
    public JsonObject Saved => (JsonObject)_saved.DeepClone();

    long Now() => _h.Now?.Invoke() ?? _clock.Time.GetUtcNow().ToUnixTimeMilliseconds();
    DateTime LocalNow() => _h.LocalNow?.Invoke() ?? DateTime.Now;

    public ValueTask InstallAsync(IFeatureContext ctx)
    {
        _ctx = ctx;
        _h = _given ?? _configured ?? new Host();
        _clock = _h.Clock ?? new HostClock();
        string profile = _h.UserProfile ?? Environment.GetEnvironmentVariable("USERPROFILE") ?? Path.GetTempPath();
        string dataRoot = _h.DataRoot ?? Path.Combine(Path.GetTempPath(), "mantle-notes-test");
        string dir;
        if (_h.Dir != null) dir = _h.Dir;
        else {
            var c = NotesPaths.Resolve(ctx.Flags.Test, profile, dataRoot, Path.GetTempPath(), Environment.GetEnvironmentVariable);
            dir = c.Dir;
            if (c.Note != null) ctx.Log(Scrub(c.Note, dir));
        }
        _safe = _h.Safe ?? new SafeFile(WriteGate.Never(NoGate));   // fail closed: no gate, no write
        _store = new NotesStore(dir, _safe, LocalNow, Now, _h.Sleep);
        _rescueDir = _h.RescueDir;
        if (_rescueDir != null) _rescued = NotesStore.ListRescue(_rescueDir).TakeLast(3).ToArray();
        _savedActive = ctx.Settings.TryGet("active", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out int ai) && ai >= 0 ? ai : 0;
        Current = this;

        ctx.Replay(StateEvent, () => El(State()));
        ctx.Command("toggle", Sync(c => Guard(() => Toggle(c.Args, Str(c.Args, "via") ?? "icon"))));
        ctx.Command("open", Sync(c => Guard(() => Open(c.Args, Str(c.Args, "via") ?? "command"))));
        ctx.Command("close", Sync(c => Guard(() => Close(Bool(c.Args, "refocus"), "command"))));
        ctx.Command("new", Sync(c => Guard(() => {
            var r = _open ? Answer(true) : Open(c.Args, "new");
            if (!_open) return r;
            string? id = Pad.NewNote();
            if (id != null) { Changed(); _ctx.Broadcast(NewEvent, El(new JsonObject { ["id"] = id })); }
            return Answer(true);
        })));
        ctx.Command("state", Sync(_ => Guard(() => CommandResult.Success(El(State())))));
        ctx.OnChord(Chord, _ => Toggle(default, "chord"));
        ctx.OnEnabled(on => {
            if (!on && _open) Close(false, "switched off");   // it will not open again until on (NOTE-012)
            Push();
        });
        if (_h.Surface) ctx.Health(true, Healthy); else ctx.Health(false, NoSurface);
        if (_carry != null) RetryCarry();
        Push();
        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(FeatureStop stop)
    {
        // stop() writes the text before anything is torn down (NOTE-093): quit, session end and a rest all come here.
        _timer?.Dispose(); _timer = null;
        if (Pad.Loaded) Pad.Prune();
        Flush(stop.Rest ? "rest" : "stop", force: false);
        if (Pad.Dirty) {
            string why = _saved["reason"]?.GetValue<string>() ?? "it could not be saved";
            if (stop.Rest) {
                _carry = new Carry(Pad.Texts().ToArray(), why);
                _ctx.Log($"{Pad.Notes.Count} notes wait unsaved until the game exits ({Scrub(why)})");
            } else Rescue(why);
        }
        _stopped = true;
        _open = false;
        if (Current == this) Current = null;
        try {
            _ctx.Broadcast(StateEvent, El(State(stop.Rest ? "resting" : "stopped")));
        } catch (Exception ex) { _ctx.Log($"the state push failed: {ex.GetType().Name}"); }
        return ValueTask.CompletedTask;
    }

    void Rescue(string why)
    {
        if (_rescueDir == null) { _ctx.Log($"NOT saved at stop ({Scrub(why)}); no rescue folder was given, so {Pad.Notes.Count} notes were lost"); return; }
        var r = NotesStore.WriteRescue(_safe, _rescueDir, Pad.Texts(), LocalNow());
        _ctx.Log(r.File != null
            ? $"NOT saved at stop ({Scrub(why)}); {Pad.Notes.Count} notes were kept in a rescue file beside the settings"
            : $"NOT saved at stop ({Scrub(why)}) and the rescue copy failed too ({Scrub(r.Reason ?? "")})");
    }


    // ---- open / close ----

    CommandResult Toggle(JsonElement a, string via)
    {
        if (Bool(a, "wasOpen") && !_open) return Answer(false, "already closed by the click that pressed the icon");
        if (!_open && via != "chord" && Now() - _blurClosedAt < BlurGraceMs) return Answer(false, "closed by the same click");
        if (_open) { string? app = Str(a, "app"); if (app != null) _refocus = app; return Close(true, via); }
        return Open(a, via);
    }

    CommandResult Open(JsonElement a, string via)
    {
        if (_stopped) return CommandResult.Fail(StoppedWords);
        if (!_ctx.Enabled) return CommandResult.Fail(OffWords);
        if (!_h.Surface) return CommandResult.Fail(NoSurface);
        if (!_open) {
            string? app = Str(a, "app");
            _refocus = app is null or "0" ? null : app;   // hwnd "0" = nothing to refocus (NOTE-009)
        }
        if (!Pad.Loaded && Pad.LoadError == null) LoadFile();
        else if (!Pad.Dirty && _store.ChangedOnDisk()) LoadFile();   // an outside change with nothing unsaved is read again (NOTE-060)
        _open = true;
        _ctx.Broadcast(OpenEvent, El(new JsonObject { ["focus"] = true }));   // opening again just re-pushes focus (NOTE-010)
        _ctx.Log($"opened ({via}): {NotesPad.Plural(Pad.Notes.Count, "note")}");
        Push();
        return Answer(true);
    }

    CommandResult Close(bool refocus, string via)
    {
        if (!_open) return Answer(false);
        _open = false;
        if (!refocus) _refocus = null;
        Pad.DropUndo();
        bool pruned = Pad.Prune();
        Flush("close", force: pruned);   // the close flushes (with the prune) before anything else (NOTE-044)
        RememberActive();
        bool ok = _saved["ok"]?.GetValue<bool>() == true;
        _ctx.Log($"closed ({via}): {Counts()}, {(ok ? "saved" : "NOT saved (" + Scrub(_saved["reason"]?.GetValue<string>() ?? "") + ")")}");
        Push();
        return Answer(false);
    }

    /// The pad lost the keyboard: unsaved text saves at once (NOTE-042). <paramref name="closes"/> = a click outside, which
    /// closes it without refocus (NOTE-007) and starts the 400 ms grace for the icon's own toggle (NOTE-004).
    public void Blurred(bool closes)
    {
        if (_stopped) return;
        if (Pad.Dirty) { _auto.Sent(); Flush("blur", force: false); }
        if (closes && _open) { _blurClosedAt = Now(); Close(false, "blur"); }
    }

    void LoadFile()
    {
        var l = _store.Load();
        Pad.Load(l.Texts, _savedActive, l.Ok ? null : l.Reason);
        if (!l.Ok) LogOnce("could not read the notes: " + Scrub(l.Reason ?? ""));
        Push();
    }

    // ---- editing and saving ----

    /// <summary>The surface's keystroke: the note's whole text. Nothing about the text is logged or broadcast.</summary>
    public bool Edit(string id, string text)
    {
        if (_stopped || !Pad.Edit(id, text)) return false;
        Changed();
        return true;
    }

    void Changed()
    {
        long now = Now();
        long due = _auto.Changed(now);
        Arm(due - now);
    }

    void Arm(long ms)
    {
        _timer?.Dispose();
        _timer = _clock.Later(TimeSpan.FromMilliseconds(Math.Max(0, ms)), Tick);
    }

    void Tick()
    {
        _timer = null;
        if (_stopped) return;
        long now = Now();
        if (_auto.Pending && !_auto.Due(now)) { Arm(_auto.DueAt!.Value - now); return; }
        _auto.Sent();
        Flush("autosave", force: false);
        if (Pad.Dirty && Pad.Loaded) Arm(NotesAutosave.RetryMs);   // a failed save is tried again every 5 s (NOTE-047)
    }

    void Flush(string why, bool force)
    {
        if (!Pad.Loaded) return;   // a file that was not opened is never written over from an empty pad
        if (!Pad.Dirty && !force) return;
        long rev = Pad.Rev;
        var r = _store.Save(Pad.Texts());
        if (r.Ok) {
            Pad.MarkSaved(rev);
            _saved = new JsonObject { ["ok"] = true, ["at"] = Now(), ["reason"] = null, ["rev"] = rev };
            if (r.Outside != null) _outside = new JsonObject { ["copy"] = r.Outside, ["name"] = Path.GetFileName(r.Outside), ["at"] = Now() };
            _loggedReasons.Clear();
        } else {
            _saved = new JsonObject { ["ok"] = false, ["at"] = null, ["reason"] = r.Reason, ["rev"] = Pad.SavedRev };
            LogOnce($"could not save ({why}): {Scrub(r.Reason ?? "")}");   // once per reason, not once per keystroke (NOTE-050)
        }
        Push();
    }

    void RetryCarry()
    {
        var c = _carry!;
        _store.Load();   // what is on disk now, so an outside change is still kept aside before the save goes over it
        Pad.Load(c.Texts, _savedActive);   // the pad holds the carried text from here on, whatever the save answers
        var r = _store.Save(c.Texts);
        if (r.Ok) {
            _carry = null;
            _saved = new JsonObject { ["ok"] = true, ["at"] = Now(), ["reason"] = null, ["rev"] = 0 };
            _ctx.Log($"saved the notes left unsaved before the rest: {Counts()}");
        } else {
            // they wait in the pad, marked unsaved with the reason; the next keystroke or the 5 s retry tries again
            _saved = new JsonObject { ["ok"] = false, ["at"] = null, ["reason"] = r.Reason ?? c.Reason, ["rev"] = 0 };
            Pad.ForceDirty();
            _carry = null;
            _ctx.Log($"the notes left unsaved before the rest still cannot be saved: {Scrub(r.Reason ?? c.Reason)}; they wait in the pad");
            Arm(NotesAutosave.RetryMs);
        }
    }

    /// <summary>Delete one note: its text goes to deleted.md first (NOTE-072); a refusal keeps the note (NOTE-074).</summary>
    public (bool Ok, string? Reason) DeleteNote(string? id)
    {
        if (_stopped || !_open) return (false, "the notes pad is not up");
        var r = Pad.Delete(id, Now(), text => { var k = _store.AppendDeleted(text); return k.Ok ? null : k.Reason; });
        if (!r.Ok) { LogOnce("a delete was refused: " + Scrub(r.Reason ?? "")); return r; }
        Flush("delete", force: true);
        return r;
    }

    public bool UndoDelete() { bool ok = Pad.UndoDelete(Now()); if (ok) Flush("undo", force: true); return ok; }

    /// The surface opened another tab: the index is remembered across runs, written only when it changes (NOTE-035).
    public void Activated(string id) { if (Pad.Activate(id)) RememberActive(); }

    void RememberActive()
    {
        int i = Pad.ActiveIndex;
        if (i == _savedActive) return;
        _savedActive = i;
        _ = _ctx.Settings.SetAsync("active", JsonValue.Create(i));
    }

    // ---- state, privacy ----

    string Counts() => $"{NotesPad.Plural(Pad.Notes.Count, "note")}, {Pad.Notes.Sum(n => n.Text.Length)} characters";

    JsonObject State(string? stateWord = null)
    {
        var o = new JsonObject {
            ["on"] = _ctx.Enabled, ["open"] = _open, ["count"] = Pad.Notes.Count,
            ["file"] = _store.File, ["folder"] = _store.Dir,
            ["saved"] = _saved.DeepClone(), ["outside"] = _outside?.DeepClone(), ["loadError"] = Pad.LoadError,
            ["rescued"] = new JsonArray(_rescued.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["windowUp"] = false, ["test"] = _ctx.Flags.Test,
        };
        if (stateWord != null) o["state"] = stateWord;
        return o;
    }

    void Push()
    {
        if (_stopped) return;
        try { _ctx.Broadcast(StateEvent, El(State())); } catch (Exception ex) { _ctx.Log($"the state push failed: {ex.GetType().Name}"); }
    }

    void LogOnce(string line) { if (_loggedReasons.Add(line)) _ctx.Log(line); }

    /// <summary>No path or file name in a log line (NOTE-094): the notes file, the notes folder, a note file.</summary>
    string Scrub(string s) => Scrub(s, _store.Dir);

    static string Scrub(string s, string dir)
    {
        string notes = Path.Combine(dir, "notes.md");
        s = Regex.Replace(s, Regex.Escape(notes) + @"(\.tmp-\d+)?", "the notes file", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, Regex.Escape(dir) + @"\\[^\r\n""]*?\.md(\.tmp-\d+)?", "a note file", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, Regex.Escape(dir), "the notes folder", RegexOptions.IgnoreCase);
        return s;
    }

    CommandResult Guard(Func<CommandResult> f)
    {
        try { return f(); }
        catch (Exception ex) { _ctx.Log($"a command threw ({ex.GetType().Name})"); return CommandResult.Fail("quick notes failed (" + ex.GetType().Name + ")"); }
    }

    // CommandHandler is async; these wrap the synchronous answers.
    CommandHandler Sync(Func<CommandCall, CommandResult> f) => c => new ValueTask<CommandResult>(f(c));

    CommandResult Answer(bool open, string? why = null)
    {
        var o = new JsonObject { ["ok"] = true, ["open"] = open, ["saved"] = _saved.DeepClone() };
        if (why != null) o["why"] = why;
        return CommandResult.Success(El(o));
    }

    static string? Str(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Bool(JsonElement a, string k)
        => a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    static JsonElement El(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement.Clone();
}
