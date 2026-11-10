using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Mantle.Core.Data;
using Mantle.Core.Logging;

namespace Mantle.Core.Look;

/// <summary>Windows' APP mode: "dark", "light", or null with plain words for why it is not known.</summary>
public readonly record struct WindowsMode(string? Mode, string? Why);

/// <summary>What <c>theme.get</c> answers (lib/theme.js view()).</summary>
public sealed record ThemeView(bool Ok, string Mode, string Theme, string? Windows, string? WindowsWhy, string? Pinned, string? Note, string? Reason);

/// <summary>What <c>theme.set</c> answers: the view plus <c>value</c>, or a refusal.</summary>
/// <c>Saved</c>: done once the save has landed and its outcome is in the view (a save behind the tell; null = nothing behind).
public sealed record ThemeSetResult(bool Ok, ThemeView? View, string? Value, string? Reason, System.Threading.Tasks.Task? Saved = null);

/// <summary>
/// C# port of lib/theme.js: Mantle's appearance mode (Match Windows / Light / Dark), saved in theme.json
/// <c>{mode}</c> through a <see cref="JsonStore"/> (so through the read-only gate), resolved against Windows'
/// app mode, with the same notes, refusals, log lines and listener rules. Host thread only.
/// Deviation: a save keeps any other key in theme.json (the store's unknown-key rule) where Electron rewrites
/// the file as <c>{mode}</c> alone; with Electron's own files the bytes are the same.
/// </summary>
public sealed class ThemeCore
{
    public static readonly string[] Modes = { "system", "light", "dark" };
    public const string DefaultMode = "system";

    public static string ModeWords(string mode) => mode switch { "light" => "Light", "dark" => "Dark", _ => "Match Windows" };

    public static bool IsMode(string? v) => v is "system" or "light" or "dark";

    /// lib/theme.js#badMode: never repeats more than 40 characters, never echoes an object.
    public static string BadMode(JsonNode? v)
    {
        string shown = v switch {
            null => "nothing",
            JsonArray => "a list",
            JsonObject => "a object",
            JsonValue jv when jv.TryGetValue(out string? s) => "\"" + (s!.Length > 40 ? s[..40] : s) + "\"",
            JsonValue jv when jv.TryGetValue(out bool _) => "a boolean",
            _ => "a number",
        };
        return "Mantle's appearance can be \"light\", \"dark\" or \"system\" (Match Windows); it was sent " + shown;
    }

    /// mode + Windows' app mode -> the theme Mantle shows. Unknown Windows = light.
    public static string Resolve(string mode, string? windows) => mode is "light" or "dark" ? mode : windows == "dark" ? "dark" : "light";

    readonly JsonStore? _file;
    readonly Func<WindowsMode> _windowsMode;
    readonly string? _override;
    readonly ILogSink _log;
    readonly List<Action<ThemeView>> _listeners = new();
    string? _loadNote, _saveNote;
    string _mode;
    WindowsMode _win;
    string _current;

    /// <param name="file">theme.json's store, or null (nowhere to save). Not read at all when <paramref name="overrideMode"/> is set.</param>
    /// <param name="windowsMode">reads Windows' app mode (<see cref="ReadWindowsMode"/> in the app); may throw</param>
    /// <param name="overrideMode">--theme= of a test run: the mode is pinned and never saved</param>
    public ThemeCore(JsonStore? file, Func<WindowsMode>? windowsMode, string? overrideMode, ILogSink log)
        : this(file, windowsMode, overrideMode, log, null) { }

    /// <param name="behind">null: a pick is saved, then listeners are told (lib/theme.js's order). Else listeners are told
    /// at once and theme.json is written behind (JsonStore.UpdateBehind, in order, still flushed to disk); the save's
    /// outcome comes back through <paramref name="behind"/>, which must run it on the theme's one thread (under its
    /// owner's lock). A failure is then the note and the log line, and listeners are told again.</param>
    public ThemeCore(JsonStore? file, Func<WindowsMode>? windowsMode, string? overrideMode, ILogSink log, Action<Action>? behind)
    {
        _behind = behind;
        _file = file;
        _windowsMode = windowsMode ?? (() => new WindowsMode(null, "Mantle was given no way to read Windows' mode"));
        _override = IsMode(overrideMode) ? overrideMode : null;
        _log = log;
        _mode = _override ?? Load();
        _win = ReadWindows();
        _current = Resolve(_mode, _win.Mode);
    }

    WindowsMode ReadWindows()
    {
        WindowsMode r;
        try { r = _windowsMode(); } catch (Exception e) { r = new WindowsMode(null, "Windows' mode could not be read (" + e.Message + ")"); }
        string? mode = r.Mode is "dark" or "light" ? r.Mode : null;
        return new WindowsMode(mode, mode != null ? null : string.IsNullOrEmpty(r.Why) ? "Windows did not say whether apps are light or dark" : r.Why);
    }

    string Load()
    {
        if (_file == null) return DefaultMode;
        string? why = null;
        if (_file.State == ReadState.Missing) return DefaultMode;
        if (_file.State != ReadState.Ok) why = (_file.Problem ?? "could not be used").Replace("does not hold settings (", "does not hold a setting (");
        else if (!_file.TryGet("mode", out var m)) return DefaultMode;
        else if (m.ValueKind == System.Text.Json.JsonValueKind.String && IsMode(m.GetString())) return m.GetString()!;
        else {
            why = "says " + BadMode(JsJson.FromElement(m)).Split(" it was sent ")[1] + ", which is not a mode";
            _file.MoveAsideNow(why);
        }
        string? kept = _file.KeptAs;
        _loadNote = $"The saved appearance was not used: {_file.Name} {why}; {(kept != null ? $"kept as {kept}, so " : "")}Mantle is back to Match Windows";
        _log.Line("theme: " + _loadNote);   // the file NAME only, never the path
        return DefaultMode;
    }

    public string Theme => _current;
    public string Mode => _mode;

    public ThemeView Get() => new(true, _mode, _current, _win.Mode,
        _win.Mode != null ? null : _win.Why + (_mode == "system" ? ", so Match Windows shows light" : ""),
        _override != null ? $"This test run was started with --theme={_override}, so a change here lasts until it quits and is never saved" : null,
        _saveNote ?? _loadNote, null);

    ThemeView Tell()
    {
        var v = Get();
        foreach (var fn in _listeners.ToArray()) {
            try { fn(Get()); } catch (Exception e) { _log.Line("theme: a listener threw: " + e.Message); }
        }
        return v;
    }

    /// <summary>theme.set: <c>{mode}</c> or a bare mode string.</summary>
    public ThemeSetResult Set(JsonNode? m)
    {
        JsonNode? want = m is JsonObject o ? (o.TryGetPropertyValue("mode", out var x) ? x : null) : m;
        string? mode = want is JsonValue jv && jv.TryGetValue(out string? s) ? s : null;
        if (!IsMode(mode)) return new ThemeSetResult(false, null, null, BadMode(want));
        string was = _current, wasMode = _mode;
        _mode = mode!;
        _win = ReadWindows();
        _current = Resolve(_mode, _win.Mode);
        System.Threading.Tasks.Task? saved = null;
        if (_override == null) {
            if (_file == null) SaveOutcome("Mantle has nowhere to save it", _mode, false);
            else if (_behind == null) SaveOutcome(_file.UpdateAsync(d => d["mode"] = _mode).Result is { Ok: false } w ? w.Reason : null, _mode, false);
            else {
                string picked = _mode;
                var write = _file.UpdateBehind(d => d["mode"] = picked);
                if (write.IsCompleted) SaveOutcome(write.Result.Ok ? null : write.Result.Reason, picked, false);   // refused before any byte
                else saved = Behind(write, picked);
            }
        }
        if (was != _current || wasMode != _mode) _log.Line($"theme: {ModeWords(_mode)} -> Mantle is {_current}");
        return new ThemeSetResult(true, Tell(), _mode, null, saved);
    }

    readonly Action<Action>? _behind;
    int _saveSeq;
    System.Threading.Tasks.Task _lastSaved = System.Threading.Tasks.Task.CompletedTask;

    /// Done when the last save behind has landed and its outcome is in the view.
    public System.Threading.Tasks.Task LastSaved => _lastSaved;

    /// theme.json's writes still on their way (the file itself; for the quit's last wait).
    public System.Threading.Tasks.Task Writes => _file?.Behind ?? System.Threading.Tasks.Task.CompletedTask;

    System.Threading.Tasks.Task Behind(System.Threading.Tasks.Task<StoreWrite> write, string picked)
    {
        _saveNote = null;   // told now as saved; a failure is told again with its note
        int id = ++_saveSeq;
        var done = new System.Threading.Tasks.TaskCompletionSource(System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        write.ContinueWith(t => {
            string? err = t.IsFaulted ? t.Exception?.GetBaseException().Message ?? "it failed" : t.Result.Ok ? null : t.Result.Reason;
            try {
                _behind!(() => {
                    try {
                        if (id == _saveSeq) SaveOutcome(err, picked, true);
                        else if (err != null) _log.Line($"theme: an earlier save ({ModeWords(picked)}) could not be written ({err}); a newer pick is being saved");
                    } finally { done.TrySetResult(); }
                });
            } catch (Exception e) {
                _log.Line("theme: the save's outcome could not be passed back (" + e.Message + ")" + (err != null ? "; it failed: " + err : ""));
                done.TrySetResult();
            }
        }, System.Threading.CancellationToken.None, System.Threading.Tasks.TaskContinuationOptions.None, System.Threading.Tasks.TaskScheduler.Default);
        return _lastSaved = done.Task;
    }

    /// The save's outcome in the view (lib/theme.js set: the note, the log line, a good save clears the load note).
    /// After a save behind, listeners hear it again only when the note moved.
    void SaveOutcome(string? err, string picked, bool tellIfMoved)
    {
        string? before = _saveNote ?? _loadNote;
        _saveNote = err != null ? $"{ModeWords(picked)} is on now, but it could not be saved ({err}), so Mantle goes back to the last saved appearance at its next start" : null;
        if (err != null) _log.Line("theme: " + _saveNote);
        else _loadNote = null;   // a good save replaces the file the note was about
        if (tellIfMoved && (_saveNote ?? _loadNote) != before) Tell();
    }

    /// <summary>theme.json changed on disk (Electron's Settings, or a test, wrote it): read the mode again and tell
    /// listeners only when the view moved. A run pinned by --theme ignores it.</summary>
    public bool FileChanged()
    {
        if (_override != null || _file == null) return false;
        if (_file.WritesBehind > 0) return false;   // our own save is landing; the in-memory pick is the newer one
        _file.Load();
        string before = $"{_mode}|{_current}";
        _mode = Load();
        _current = Resolve(_mode, _win.Mode);
        if ($"{_mode}|{_current}" == before) return false;
        _log.Line($"theme: {_file.Name} changed: {ModeWords(_mode)} -> Mantle is {_current}");
        Tell();
        return true;
    }

    /// Windows' app mode may have changed: re-read it; tell listeners only when the view moved.
    public bool WindowsChanged()
    {
        string before = $"{_win.Mode}|{_win.Why}|{_current}";
        _win = ReadWindows();
        _current = Resolve(_mode, _win.Mode);
        if ($"{_win.Mode}|{_win.Why}|{_current}" == before) return false;
        _log.Line($"theme: Windows apps are {_win.Mode ?? "unknown"} now" + (_mode == "system" ? $", so Mantle is {_current}" : $" (Mantle stays {_current}: it is set to {ModeWords(_mode)})"));
        Tell();
        return true;
    }

    public IDisposable On(Action<ThemeView> fn)
    {
        if (fn == null) throw new ArgumentNullException(nameof(fn), "theme.on needs a function");
        _listeners.Add(fn);
        return new Off(() => _listeners.Remove(fn));
    }

    sealed class Off : IDisposable
    {
        Action? _a;
        public Off(Action a) => _a = a;
        public void Dispose() { _a?.Invoke(); _a = null; }
    }

    /// The key Windows keeps the app mode in (under HKCU), and the value nativeTheme follows.
    public const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string AppsUseLightTheme = "AppsUseLightTheme";

    /// <summary>Windows' app mode from HKCU\...\Themes\Personalize AppsUseLightTheme (what nativeTheme follows).</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsMode ReadWindowsMode() => ReadWindowsMode(PersonalizeKey);

    /// <summary>The same from another key under HKCU: a test run's scratch key (MANTLE_TEST_THEME_KEY), never the user's own.</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsMode ReadWindowsMode(string subKey) => ReadWindowsMode(() => {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);
        return k?.GetValue(AppsUseLightTheme);
    });

    /// <summary>The key Match Windows watches in this run: <c>MANTLE_TEST_THEME_KEY</c> (a key under HKCU) in a --test run
    /// that sets it, so a UI test flips Windows' mode without touching the user's; else Windows' own Personalize key.
    /// A live run never takes the variable.</summary>
    public static string KeyFor(bool test, Func<string, string?> env)
    {
        string? k = test ? env("MANTLE_TEST_THEME_KEY") : null;
        return string.IsNullOrWhiteSpace(k) ? PersonalizeKey : k.Trim().TrimStart('\\');
    }

    /// <summary>The app mode from the registry value <paramref name="readValue"/> answers (tests fake it): a DWORD 0 is dark,
    /// any other DWORD light; no value (or not a DWORD) is Windows' default, light apps. A read that throws is the caller's
    /// to report (ThemeCore says "Windows' mode could not be read (...)").</summary>
    public static WindowsMode ReadWindowsMode(Func<object?> readValue)
    {
        object? v = readValue();
        if (v is int i) return new WindowsMode(i == 0 ? "dark" : "light", null);
        return new WindowsMode("light", null);   // no value = Windows' default, light apps
    }
}
