using System;
using System.IO;

namespace Mantle.Core;

/// <summary>
/// Where this instance's data lives. The data root is Electron Mantle's userData, resolved the way
/// main.js does it, so the native readers see the files Electron writes:
///
///   live      %APPDATA%\mantle                      (Electron's userData for the app name "mantle")
///   --test    %TEMP%\mantle-test-&lt;profile-name&gt;      (main.js: os.tmpdir() + 'mantle-test-' + name)
///   --user-data=&lt;dir&gt;  that folder, for tests and fixtures (wins over both)
///
/// The shared look (look.json) is NOT under the data root: lib/look-sync.js keeps it in
/// %APPDATA%\look-sync, or LOOK_SYNC_DIR; a --test run uses &lt;root&gt;\look-sync (main.js, so a test
/// never reads or follows the user's real shared look). --user-data does the same.
///
/// The only file native writes under the root is its own log, mantle-native.log, next to
/// Electron's mantle.log (tools/install-autostart.ps1 puts that one at %APPDATA%\mantle\mantle.log).
/// </summary>
public sealed class DataPaths
{
    public const string LogFileName = "mantle-native.log";

    /// Electron's userData for this run.
    public string DataRoot { get; }
    /// The folder of the shared look.json (Helm, Quartz and Mantle).
    public string LookSyncDir { get; }
    /// Native's own log file.
    public string LogFile { get; }
    /// True when the root is the user's real profile (not --test, not --user-data).
    public bool IsUserProfile { get; }
    /// One line for the log: which root and why.
    public string Why { get; }

    DataPaths(string root, string lookDir, bool user, string why)
    {
        DataRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        LookSyncDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lookDir));
        LogFile = Path.Combine(DataRoot, LogFileName);
        IsUserProfile = user;
        Why = why;
    }

    /// <param name="args">parsed and valid (args.Ok)</param>
    /// <param name="env">environment lookup (Environment.GetEnvironmentVariable in the app)</param>
    /// <param name="tempDir">the temp folder (Path.GetTempPath() in the app)</param>
    public static DataPaths Resolve(LaunchArgs args, Func<string, string?> env, string tempDir)
    {
        string appData = env("APPDATA") is { Length: > 0 } ad ? ad : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string? lookEnv = env("LOOK_SYNC_DIR") is { Length: > 0 } ls ? ls : null;
        if (args.UserData != null) {
            string root = args.UserData;
            return new DataPaths(root, lookEnv ?? Path.Combine(root, "look-sync"), false, "--user-data");
        }
        if (args.Test) {
            string root = Path.Combine(tempDir, "mantle-test-" + args.ProfileName);
            return new DataPaths(root, lookEnv ?? Path.Combine(root, "look-sync"), false, "--test --profile-name=" + args.ProfileName);
        }
        return new DataPaths(Path.Combine(appData, "mantle"), lookEnv ?? Path.Combine(appData, "look-sync"), true, "the user's profile");
    }
}
