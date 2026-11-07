using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Mantle.Core.Logging;

/// <summary>
/// THE LOG FILE NAMES NEITHER THE PERSON NOR THE PC: main.js makeScrubber, ported rule for rule.
/// Every path under the home folder becomes %USERPROFILE% (with \, / or the \\ of a JSON string as
/// separators), the user name anywhere else in a path becomes &lt;user&gt;, and the computer's name
/// becomes &lt;pc&gt;. It runs on every line the file gets.
/// </summary>
public sealed class LogScrubber
{
    readonly List<(Regex Re, string To)> _rules = new();

    public LogScrubber(string? home, string? user, string? host)
    {
        string h = (home ?? "").TrimEnd('\\', '/');
        if (h.Length > 3) {
            var parts = h.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            const string sep = @"(?:\\\\|\\|/)";
            var forms = new List<string> { string.Join(sep, Array.ConvertAll(parts, Regex.Escape)) };
            var enc = Array.ConvertAll(parts, p => Uri.EscapeDataString(p).Replace("%3A", ":"));
            if (string.Join("/", enc) != string.Join("/", parts)) forms.Add(string.Join("/", Array.ConvertAll(enc, Regex.Escape)));
            foreach (var f in forms) _rules.Add((new Regex(f + "(?![A-Za-z0-9_.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "%USERPROFILE%"));
        }
        if (user is { Length: >= 2 })
            _rules.Add((new Regex(@"([\\/])" + Regex.Escape(user) + @"(?=[\\/""'\s:;,)]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "$1<user>"));
        if (host is { Length: >= 3 })
            _rules.Add((new Regex("(^|[^A-Za-z0-9_-])" + Regex.Escape(host) + "(?![A-Za-z0-9_-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), "$1<pc>"));
    }

    /// The scrubber for this process: %USERPROFILE%, the user name and the computer name.
    public static LogScrubber ForThisMachine()
        => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

    public string Scrub(string text)
    {
        string s = text;
        foreach (var (re, to) in _rules) s = re.Replace(s, to);
        return s;
    }
}
