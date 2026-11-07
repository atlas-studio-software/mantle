using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Mantle.Core.Logging;

/// Anything that takes log lines. MantleLog is the real one; tests use their own.
public interface ILogSink
{
    /// One line (or several, split on \n), without the "mantle-native: " prefix.
    void Line(string text);
}

public sealed class NullLog : ILogSink
{
    public static readonly NullLog Instance = new();
    public void Line(string text) { }
}

public sealed class LogOptions
{
    public long MaxBytes = 5 * 1024 * 1024;
    public int Keep = 2;
    public long RetryBytes = 1024 * 1024;
    public int ReopenMs = 5000;
    public Func<DateTime> Now = () => DateTime.Now;
    public Func<double> ClockMs = () => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    /// Where the log's own trouble goes when the file cannot take it (stderr in the app).
    public Action<string> Say = s => { try { Console.Error.Write(s); } catch (IOException) { } };
    public LogScrubber? Scrub;
    /// The bench's copy (--bench-out\native-log.txt), in the spike's format: "HH:mm:ss.fff text".
    public string? MirrorFile;
    /// LOG-001: with --log=, every line also goes to the terminal (stdout), as Electron's tee does.
    public Action<string>? Echo;
}

/// <summary>
/// Native Mantle's log, in Electron Mantle's format (main.js makeLogTee, the --log=&lt;path&gt; file
/// that the logon task points at %APPDATA%\mantle\mantle.log). Native writes mantle-native.log
/// beside it, with the same rules:
///   - every line starts with the local time "HH:mm:ss.fff "; the first line of a run and the first
///     after midnight carry the date too ("yyyy-MM-dd HH:mm:ss.fff ");
///   - a run starts with a blank line (when the file is not empty) and a "===== ... start" header;
///   - at 5 MB the file becomes &lt;file&gt;.1 (.1 becomes .2, the old .2 goes), always between two
///     lines; a rotation that fails keeps writing to the same file, says so IN the log, and tries
///     again 1 MB later; a file that cannot be reopened is retried every 5 s and the first line
///     back says how long and how many writes were lost;
///   - every line is scrubbed (LogScrubber): no user name, home path or computer name;
///   - it never takes the app down: a log that cannot be written is said on stderr and skipped.
/// Lines are "mantle-native: &lt;text&gt;", where Electron's are "mantle: &lt;text&gt;".
/// Thread-safe.
/// </summary>
public sealed class MantleLog : ILogSink, IDisposable
{
    public const string Prefix = "mantle-native: ";

    readonly string _file;
    readonly LogOptions _o;
    readonly object _gate = new();
    FileStream? _fs;
    long _size;
    long _rotateAt;
    string _lastDay = "";
    Lost? _lost;
    bool _mirrorFailed;

    sealed class Lost { public double Since, Tried; public int Writes; public string Reason = ""; }

    public string File => _file;
    public long Size { get { lock (_gate) return _size; } }

    public MantleLog(string file, LogOptions? options = null)
    {
        _file = file;
        _o = options ?? new LogOptions();
        _rotateAt = _o.MaxBytes;
    }

    /// Open the file (creating its folder), rotate it if it is already over the limit, and write
    /// the run's header. False (and said on stderr) when the file cannot be opened at all.
    public bool Start(string header)
    {
        lock (_gate) {
            try {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                Open();
            } catch (Exception e) {
                _o.Say($"{Prefix}the log file cannot be written ({e.Message}); logging to stderr only\n");
                _fs = null;
                return false;
            }
            if (_size >= _o.MaxBytes) Rotate("rotated at start");
            if (_fs != null && _size > 0) RawWrite("\n");   // a blank line between runs, as Electron's
            Put(header);
            Mirror(header);
            return true;
        }
    }

    public void Line(string text)
    {
        if (text == null) return;
        foreach (var one in text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
            Write(Prefix + one);
    }

    /// One already-prefixed line (the header and the log's own notes use this).
    public void Write(string line)
    {
        lock (_gate) {
            Mirror(line);
            if (_fs == null) {
                if (_lost == null) return;
                double t = _o.ClockMs();
                if (t - _lost.Tried < _o.ReopenMs) { _lost.Writes++; return; }
                _lost.Tried = t;
                try { Open(); } catch (Exception) { _fs = null; _lost.Writes++; return; }
                var gone = _lost;
                _lost = null;
                Put($"{Prefix}log: this file could not be opened for {((t - gone.Since) / 1000).ToString("0", CultureInfo.InvariantCulture)} s ({gone.Reason}); {gone.Writes} writes to it were lost");
            }
            Put(line);
            if (_size >= _rotateAt) Rotate($"rotated at {(_o.MaxBytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture)} MB");
        }
    }

    void Open()
    {
        _fs = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.None);
        _size = _fs.Length;
    }

    string Stamp()
    {
        var d = _o.Now();
        string day = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string time = d.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        if (day == _lastDay) return time + " ";
        _lastDay = day;
        return day + " " + time + " ";
    }

    void Put(string line)
    {
        if (_fs == null) return;
        string text = line;
        if (_o.Scrub != null) { try { text = _o.Scrub.Scrub(text); } catch (Exception) { /* the line as it was */ } }
        RawWrite(Stamp() + text + "\n");
    }

    void RawWrite(string s)
    {
        if (_fs == null) return;
        byte[] b = Encoding.UTF8.GetBytes(s);
        try {
            _fs.Write(b, 0, b.Length);
            _fs.Flush();
            _size += b.Length;
        } catch (Exception e) {
            _o.Say($"{Prefix}a log write failed ({e.Message})\n");
        }
    }

    void Mirror(string line)
    {
        if (_o.Echo != null) { try { _o.Echo(line); } catch (Exception) { /* a closed terminal never stops the log */ } }
        if (_o.MirrorFile == null || _mirrorFailed) return;
        try {
            string text = _o.Scrub != null ? _o.Scrub.Scrub(line) : line;
            if (text.StartsWith(Prefix, StringComparison.Ordinal)) text = text[Prefix.Length..];
            System.IO.File.AppendAllText(_o.MirrorFile, _o.Now().ToString("HH:mm:ss.fff ", CultureInfo.InvariantCulture) + text + Environment.NewLine);
        } catch (Exception e) {
            _mirrorFailed = true;
            _o.Say($"{Prefix}the bench copy of the log ({Path.GetFileName(_o.MirrorFile)}) cannot be written ({e.Message}); it stops here\n");
        }
    }

    void Rotate(string why)
    {
        double t0 = _o.ClockMs();
        long was = _size;
        try { _fs?.Dispose(); } catch (Exception) { /* reopened below either way */ }
        _fs = null;
        string? failed = null;
        string name = Path.GetFileName(_file);
        for (int k = _o.Keep; k >= 1 && failed == null; k--) {
            string from = k == 1 ? _file : $"{_file}.{k - 1}";
            try {
                if (System.IO.File.Exists(from)) System.IO.File.Move(from, $"{_file}.{k}", overwrite: true);
            } catch (Exception e) {
                failed = $"{Path.GetFileName(from)} -> .{k}: {e.GetType().Name}";
            }
        }
        try { Open(); } catch (Exception e) {
            _fs = null;
            _lost = new Lost { Since = _o.ClockMs(), Tried = _o.ClockMs(), Writes = 0, Reason = e.GetType().Name };
            _o.Say($"{Prefix}the log file could not be reopened after rotating it ({e.Message}); logging to stderr only, trying again every {_o.ReopenMs / 1000} s\n");
            return;
        }
        double ms = _o.ClockMs() - t0;
        _rotateAt = failed != null ? _size + _o.RetryBytes : _o.MaxBytes;
        string mb(long n) => (n / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
        string note = failed != null
            ? $"{Prefix}log: could NOT rotate at {mb(was)} MB ({failed}); still writing to this file, next try at {mb(_rotateAt)} MB"
            : $"{Prefix}log: {why}: the previous {mb(was)} MB is now {name}.1 ({_o.Keep} old files kept), rotated in {ms.ToString("0.0", CultureInfo.InvariantCulture)} ms";
        Put(note);
        if (failed != null) _o.Say(note + "\n");
    }

    public void Dispose()
    {
        lock (_gate) {
            try { _fs?.Dispose(); } catch (Exception) { }
            _fs = null;
        }
    }
}
