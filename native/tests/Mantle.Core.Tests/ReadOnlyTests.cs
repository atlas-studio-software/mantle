using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Mantle.Core.Data;
using Xunit;

namespace Mantle.Core.Tests;

/// <summary>
/// Native NEVER writes Electron's data. Three proofs: the API has no way to write, the source of
/// Mantle.Core.Data names no writing call, and a profile on disk (including damaged files, which
/// Electron itself would move aside) is byte-for-byte unchanged after every reader and the watcher
/// have run, with no file added.
/// </summary>
public class ReadOnlyTests
{
    static readonly string[] WriteVerbs = { "Write", "Save", "Set", "Delete", "Remove", "Move", "Create", "Rename", "Put", "Store", "Update", "Append", "Copy", "Replace", "Publish" };

    [Fact]
    public void The_data_api_has_no_writing_method()
    {
        // IStore is round 2's writing CONTRACT (PLAN.md 3.7 (a), native/docs/contracts.md): an interface
        // with no implementation in Mantle.Core.Data yet, so the readers are still read-only. The lane
        // that implements it (native-data-look) decides how this guard changes when the stores land.
        var types = typeof(MantleData).Assembly.GetTypes()
            .Where(t => t.Namespace == "Mantle.Core.Data" && (t.IsPublic || t.IsNestedPublic) && t != typeof(IStore));
        var bad = new List<string>();
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (m.IsSpecialName) continue;   // property accessors (records' init-only ones included)
                if (WriteVerbs.Any(v => m.Name.StartsWith(v, StringComparison.Ordinal))) bad.Add(t.Name + "." + m.Name);
            }
        Assert.True(bad.Count == 0, "methods that sound like writing: " + string.Join(", ", bad));
        // and no public settable property (records' init-only setters are fine: they only build values)
        foreach (var t in types)
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) {
                var set = p.GetSetMethod();
                if (set == null) continue;
                bool initOnly = set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
                Assert.True(initOnly, $"{t.Name}.{p.Name} has a public setter");
            }
    }

    static string SourceDir([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Mantle.Core", "Data"));

    [Fact]
    public void The_data_source_calls_nothing_that_writes()
    {
        string dir = SourceDir();
        Assert.True(Directory.Exists(dir), "the Mantle.Core\\Data source was not found next to the tests (" + dir + "); this test reads it");
        string[] forbidden = {
            "File.Write", "File.Create", "File.Move", "File.Delete", "File.Copy", "File.Replace", "File.Append", "File.Open(",
            "File.SetAttributes", "File.SetLastWriteTime", "File.SetCreationTime", "File.Encrypt",
            "FileAccess.Write", "FileAccess.ReadWrite", "FileMode.Create", "FileMode.Append", "FileMode.OpenOrCreate", "FileMode.Truncate", "FileMode.CreateNew",
            "Directory.Create", "Directory.Delete", "Directory.Move",
            "StreamWriter", "BinaryWriter", ".MoveTo(", ".CopyTo(", ".Delete(", "WriteAllText", "WriteAllBytes", "WriteAllLines",
            "OpenWrite", "AppendText", "CreateText",
        };
        var hits = new List<string>();
        foreach (var f in Directory.GetFiles(dir, "*.cs")) {
            var lines = File.ReadAllLines(f);
            for (int i = 0; i < lines.Length; i++) {
                string code = lines[i];
                int c = code.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0) code = code[..c];
                foreach (var w in forbidden)
                    if (code.Contains(w, StringComparison.Ordinal)) hits.Add($"{Path.GetFileName(f)}:{i + 1}: {w}");
            }
        }
        Assert.True(hits.Count == 0, "writing calls in Mantle.Core.Data: " + string.Join("; ", hits));
        Assert.True(Directory.GetFiles(dir, "*.cs").Length >= 4, "expected the reader sources in " + dir);
    }

    static Dictionary<string, (string Hash, DateTime Written, FileAttributes Attr)> Snapshot(string root)
    {
        var d = new Dictionary<string, (string, DateTime, FileAttributes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(root, "*", SearchOption.AllDirectories)) {
            byte[] bytes;
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }
            d[Path.GetRelativePath(root, f)] = (Convert.ToHexString(SHA256.HashData(bytes)), File.GetLastWriteTimeUtc(f), File.GetAttributes(f));
        }
        foreach (var sub in Directory.GetDirectories(root, "*", SearchOption.AllDirectories)) d["dir:" + Path.GetRelativePath(root, sub)] = ("", DateTime.MinValue, FileAttributes.Directory);
        return d;
    }

    [Fact]
    public void A_profile_is_byte_for_byte_unchanged_after_reading_and_watching_even_when_damaged()
    {
        using var d = new TestDir("ro");
        d.CopyFixtureProfile();
        d.Write("theme.json", "{\"mode\":");                     // Electron would move this aside as theme.json.unreadable
        d.Write("features.json", "[1,2]");                        // and this one as features.json.unreadable
        d.Write(@"look-sync\look.json", "{\"schema\":9,\"rev\":1}");   // newer schema: read-only for look-sync too
        foreach (var f in Directory.GetFiles(d.Path, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.ReadOnly);
        var before = Snapshot(d.Path);

        var paths = DataPaths.Resolve(LaunchArgs.Parse(new[] { "--user-data=" + d.Path }), _ => null, Path.GetTempPath());
        using (var data = new MantleData(paths, new ListLog())) {
            data.Load();
            Assert.Equal(ReadState.Corrupt, data.Theme.State);
            Assert.Equal(ReadState.Newer, data.SharedLook.State);
            Assert.Equal(ReadState.Ok, data.LocalLook.State);   // read-only files still read: nothing asks for write access
            data.StartWatching(TimeSpan.FromMilliseconds(200));
            System.Threading.Thread.Sleep(900);
            data.CheckNow();
            _ = data.EffectiveGlass;
        }
        var after = Snapshot(d.Path);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));   // no file or folder added or removed
        foreach (var k in before.Keys) Assert.True(before[k] == after[k], "changed: " + k);
    }

    [Fact]
    public void A_missing_data_root_is_not_created()
    {
        using var d = new TestDir("ro");
        string root = Path.Combine(d.Path, "never-made");
        var paths = DataPaths.Resolve(LaunchArgs.Parse(new[] { "--user-data=" + root }), _ => null, Path.GetTempPath());
        using (var data = new MantleData(paths, new ListLog())) {
            data.Load();
            data.StartWatching(TimeSpan.FromMilliseconds(200));
            System.Threading.Thread.Sleep(500);
        }
        Assert.False(Directory.Exists(root));
    }
}
