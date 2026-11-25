using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Mantle.Core.Tests.Boot;

/// <summary>
/// lane r12-app-wiring: the Mantle.App side of lane r12-boot, pinned from the sources (Mantle.App is not referenced by the
/// unit tests, and the live start cannot run here). (1) Program.cs takes the live-shell lock in the --live branch, after the
/// two-shells refusal and before the live lines, and lets it go in Main's finally before the single-instance claim.
/// (2) The way back without Mantle: every script restore-windows.ps1 calls is copied next to MantleNative.exe
/// (Mantle.App.csproj MantleRestoreScript) and checked by publish.ps1, the same list in all three places.
/// </summary>
public class AppWiringTests
{
    static string NativeDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));
    static string RepoDir => Path.GetFullPath(Path.Combine(NativeDir(), ".."));
    static string Read(params string[] parts) => File.ReadAllText(Path.Combine(parts));

    // ------------------------------------------------------------------ the live-shell lock

    [Fact]
    public void Program_takes_the_live_lock_after_the_two_shells_refusal_and_before_the_live_lines()
    {
        string src = Read(NativeDir(), "src", "Mantle.App", "Program.cs");
        int liveBranch = src.IndexOf("if (args.Kind == RunKind.Live) {", StringComparison.Ordinal);
        int twoShells = src.IndexOf("TwoShells.Verdict(", StringComparison.Ordinal);
        int take = src.IndexOf("Mantle.Core.Safety.LiveLock.TryTake(", StringComparison.Ordinal);
        int held = src.IndexOf("Host.LiveLock = live;", StringComparison.Ordinal);
        int lines = src.IndexOf("StartLines.LiveLines(", StringComparison.Ordinal);
        Assert.True(liveBranch > 0 && twoShells > liveBranch, "the --live branch and its two-shells refusal");
        Assert.True(take > twoShells, "LiveLock.TryTake comes after the two-shells refusal");
        Assert.True(held > take && lines > held, "the holder is kept in Host.LiveLock before the live lines are printed");
        Assert.Equal(1, Regex.Matches(src, @"LiveLock\.TryTake\(").Count);   // one take, in the live branch only

        // a refusal exits Failed and lets the claim and the log go, like the two-shells refusal
        string refusal = src.Substring(take, held - take);
        Assert.Contains("if (live == null)", refusal);
        Assert.Contains("claim.Dispose();", refusal);
        Assert.Contains("return ExitCodes.Failed;", refusal);

        // Host holds it; Main's finally (the same thread) disposes it before the claim
        Assert.Contains("public static IDisposable? LiveLock;", src);
        int fin = src.LastIndexOf("} finally {", StringComparison.Ordinal);
        Assert.True(fin > held, "Main's finally");
        string tail = src.Substring(fin);
        int release = tail.IndexOf("Host.LiveLock?.Dispose();", StringComparison.Ordinal);
        int claim = tail.IndexOf("claim.Dispose();", StringComparison.Ordinal);
        Assert.True(release > 0 && claim > release, "Host.LiveLock?.Dispose() runs in the finally before claim.Dispose()");
    }

    // ------------------------------------------------------------------ the restore scripts next to the exe

    /// What restore-windows.ps1 runs from its own folder: <c>Join-Path $PSScriptRoot 'x.ps1'</c>.
    static string[] CalledByRestore()
    {
        string src = Read(RepoDir, "tools", "restore-windows.ps1");
        return Regex.Matches(src, @"Join-Path \$PSScriptRoot '([A-Za-z0-9._-]+)'").Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static string[] CopiedByCsproj()
    {
        var doc = XDocument.Load(Path.Combine(NativeDir(), "src", "Mantle.App", "Mantle.App.csproj"));
        var items = doc.Descendants("MantleRestoreScript").ToArray();
        Assert.Single(items);
        string inc = items[0].Attribute("Include")!.Value;
        // the copy itself: a None item over the list, linked into tools\, copied on build AND publish
        var none = doc.Descendants("None").Single(n => (string?)n.Attribute("Include") == "@(MantleRestoreScript)");
        Assert.Equal(@"tools\%(Filename)%(Extension)", (string?)none.Attribute("Link"));
        Assert.Equal("PreserveNewest", (string?)none.Attribute("CopyToOutputDirectory"));
        Assert.Equal("PreserveNewest", (string?)none.Attribute("CopyToPublishDirectory"));
        return inc.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => {
            Assert.StartsWith(@"..\..\..\tools\", p);   // the repo's one copy, never a second one
            Assert.True(File.Exists(Path.Combine(NativeDir(), "src", "Mantle.App", p.Replace('\\', Path.DirectorySeparatorChar))), p + " exists");
            return Path.GetFileName(p.Replace('\\', '/'));
        }).ToArray();
    }

    static string[] CheckedByPublish()
    {
        string src = Read(NativeDir(), "tools", "publish.ps1");
        var m = Regex.Match(src, @"\$restoreSet = @\(([^)]*)\)");
        Assert.True(m.Success, "publish.ps1 has $restoreSet");
        return Regex.Matches(m.Groups[1].Value, @"'([^']+)'").Select(x => x.Groups[1].Value).ToArray();
    }

    [Fact]
    public void Every_script_restore_windows_calls_is_copied_next_to_the_exe_and_checked_by_publish()
    {
        var called = CalledByRestore();
        Assert.Contains("ramreclaim-undo.ps1", called);    // what the read on 2026-11-25 found; more may come
        Assert.Contains("restore-banners.ps1", called);
        Assert.Contains("restore-osd.ps1", called);
        var copied = CopiedByCsproj();
        var checkedSet = CheckedByPublish();
        var need = called.Concat(new[] { "restore-windows.ps1", "restore-windows.cmd" }).ToArray();
        foreach (var n in need) {
            Assert.Contains(n, copied, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(n, checkedSet, StringComparer.OrdinalIgnoreCase);
        }
        Assert.Equal(copied.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), checkedSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_exe_finds_its_restore_script_where_the_build_puts_it()
    {
        string host = Read(NativeDir(), "src", "Mantle.App", "Program.cs");
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, \"tools\", \"restore-windows.ps1\")", host);
        // the published folder from this run's build stage (ci.ps1 -Out native/out), when there is one
        string bin = Path.Combine(NativeDir(), "out", "bin", "MantleNative");
        if (!File.Exists(Path.Combine(bin, "MantleNative.exe"))) return;
        foreach (var n in CopiedByCsproj())
            Assert.True(File.Exists(Path.Combine(bin, "tools", n)), $"native/out/bin/MantleNative/tools/{n} is next to the published exe");
    }
}
