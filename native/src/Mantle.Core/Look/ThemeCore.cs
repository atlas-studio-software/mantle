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
public sealed record ThemeSetResult(bool Ok, ThemeView? View, string? Value, string? Reason);

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
    {
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
        if (_override == null) {
            string? err = _file == null ? "Mantle has nowhere to save it"
                : _file.UpdateAsync(d => d["mode"] = _mode).Result is { Ok: false } w ? w.Reason : null;
            _saveNote = err != null ? $"{ModeWords(_mode)} is on now, but it could not be saved ({err}), so Mantle goes back to the last saved appearance at its next start" : null;
            if (err != null) _log.Line("theme: " + _saveNote);
            else _loadNote = null;   // a good save replaces the file the note was about
        }
        if (was != _current || wasMode != _mode) _log.Line($"theme: {ModeWords(_mode)} -> Mantle is {_current}");
        return new ThemeSetResult(true, Tell(), _mode, null);
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

    /// <summary>Windows' app mode from HKCU\...\Themes\Personalize AppsUseLightTheme (what nativeTheme follows).</summary>
    [SupportedOSPlatform("windows")]
    public static WindowsMode ReadWindowsMode()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        object? v = k?.GetValue("AppsUseLightTheme");
        if (v is int i) return new WindowsMode(i == 0 ? "dark" : "light", null);
        return new WindowsMode("light", null);   // no value = Windows' default, light apps
    }
}
