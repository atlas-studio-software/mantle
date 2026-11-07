using System;
using System.IO;

namespace Mantle.Core.Data;

/// <summary>
/// THE READ-ONLY GATE (native/docs/contracts.md, "Coordinator decisions for round 2"). Until the switch-over,
/// native must never write the user's real Electron data. Every file write native makes under the data root or
/// the shared look folder goes through <see cref="SafeFile"/>, and SafeFile asks this gate first:
///
///   - a test or scratch profile (<c>--test</c>, or <c>--user-data</c> that is not the user's own folder): allowed;
///   - the user's own profile (<c>%APPDATA%\mantle</c>, <c>%APPDATA%\look-sync</c>): allowed ONLY when the app runs
///     <c>--live</c> AND is the only shell (Electron Mantle is not running). Anything else is refused in words,
///     and the change stays in memory for this run only;
///   - any path outside those two folders: always refused.
///
/// The gate fails closed: when nobody has told it whether another shell is running, it assumes one is.
/// ReadOnlyTests proves that no write path skips it.
/// </summary>
public sealed class WriteGate
{
    readonly string? _dataRoot;
    readonly string? _lookDir;
    readonly bool _userData;
    readonly bool _userLook;
    readonly bool _live;
    readonly Func<string?> _otherShell;
    string? _never;

    WriteGate(string? dataRoot, string? lookDir, bool userData, bool userLook, bool live, Func<string?> otherShell)
    {
        _dataRoot = dataRoot == null ? null : Norm(dataRoot);
        _lookDir = lookDir == null ? null : Norm(lookDir);
        _userData = userData;
        _userLook = userLook;
        _live = live;
        _otherShell = otherShell;
    }

    /// <summary>The gate for this run.</summary>
    /// <param name="otherShell">null when native Mantle is the only shell, else plain words naming the other one
    /// ("Electron Mantle is running"). Omitted = not known, which counts as another shell (fail closed).</param>
    public static WriteGate For(DataPaths paths, LaunchArgs args, Func<string?>? otherShell = null)
        => new(paths.DataRoot, paths.LookSyncDir, paths.IsUserProfile, paths.IsUserLook, args.Live,
               otherShell ?? (() => "nothing has confirmed that Electron Mantle is not running"));

    /// A gate that allows writes only under <paramref name="root"/> (tests and fixtures).
    public static WriteGate Scratch(string root) => new(root, root, false, false, false, () => null);

    /// A gate that refuses everything with the given reason (tests).
    public static WriteGate Never(string why) => new(null, null, true, true, false, () => why) { _never = why };

    static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    static bool Under(string path, string? dir)
        => dir != null && (path.Equals(dir, StringComparison.OrdinalIgnoreCase)
                           || path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>Null when a write to <paramref name="fullPath"/> may go ahead; else why not, in plain words.</summary>
    public string? Refusal(string fullPath)
    {
        if (_never != null) return _never;
        string p;
        try { p = Norm(fullPath); } catch (Exception e) { return "the path could not be checked (" + e.GetType().Name + ")"; }
        bool inData = Under(p, _dataRoot), inLook = Under(p, _lookDir);
        string name = Path.GetFileName(p);
        if (!inData && !inLook) return name + " is outside Mantle's data folders, so native Mantle never writes it";
        bool user = (inData && _userData) || (inLook && _userLook);
        if (!user) return null;
        if (!_live) return "this run is not --live, so the user's own " + name + " is left exactly as Electron Mantle wrote it";
        string? other = _otherShell();
        if (other != null) return other + ", so the user's own " + name + " is left for it (native writes only as the only shell)";
        return null;
    }

    /// True when writes to the user's own profile would be allowed right now (for the log line at start).
    public string Describe()
    {
        if (!_userData && !_userLook) return "writes go to this run's own profile only";
        if (!_live) return "read-only: this run is not --live, so the user's settings are never written";
        string? other = _otherShell();
        return other != null ? "read-only: " + other : "writing the user's settings (--live, the only shell)";
    }
}
