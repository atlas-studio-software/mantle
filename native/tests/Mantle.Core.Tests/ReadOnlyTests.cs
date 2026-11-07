using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mantle.Core.Data;
using Xunit;

namespace Mantle.Core.Tests;

/// <summary>
/// THE READ-ONLY GATE (contracts.md, coordinator decision on ReadOnlyTests): until the switch-over native
/// never writes the user's real Electron data. Writes exist (the stores, look-sync), but every one goes
/// through <see cref="SafeFile"/>, which asks <see cref="WriteGate"/> first. These tests fail on any write
/// path that skips the gate: a writing call anywhere in Mantle.Core.Data or Mantle.Core.Look outside
/// SafeFile.cs, a SafeFile method that writes when the gate says no, a reader type that grows a writing
/// method, or a profile that changes when the gate is closed.
/// </summary>
public class ReadOnlyTests
{
    static readonly string[] WriteVerbs = { "Write", "Save", "Set", "Delete", "Remove", "Move", "Create", "Rename", "Put", "Store", "Update", "Append", "Copy", "Replace", "Publish" };

    /// The gated writers: everything they write goes through SafeFile (proved below). JsJson.WriteString
    /// writes into a StringBuilder, not a file.
    static readonly HashSet<Type> GatedWriters = new() {
        typeof(IStore), typeof(IStores), typeof(JsonStore), typeof(Stores), typeof(SafeFile), typeof(WriteGate), typeof(JsJson),
    };

    [Fact]
    public void The_reader_api_has_no_writing_method()
    {
        var types = typeof(MantleData).Assembly.GetTypes()
            .Where(t => t.Namespace == "Mantle.Core.Data" && (t.IsPublic || t.IsNestedPublic) && !GatedWriters.Contains(t));
        var bad = new List<string>();
        foreach (var t in types)
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                if (m.IsSpecialName) continue;   // property accessors (records' init-only ones included)
                if (WriteVerbs.Any(v => m.Name.StartsWith(v, StringComparison.Ordinal))) bad.Add(t.Name + "." + m.Name);
            }
        Assert.True(bad.Count == 0, "methods that sound like writing outside the gated writers: " + string.Join(", ", bad));
        // and no public settable property (records' init-only setters are fine: they only build values)
        foreach (var t in types)
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)) {
                var set = p.GetSetMethod();
                if (set == null) continue;
                bool initOnly = set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
                Assert.True(initOnly, $"{t.Name}.{p.Name} has a public setter");
            }
    }

    static string SourceDir(string sub, [CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src", "Mantle.Core", sub));

    static readonly string[] Forbidden = {
        "File.Write", "File.Create", "File.Move", "File.Delete", "File.Copy", "File.Replace", "File.Append", "File.Open(",
        "File.SetAttributes", "File.SetLastWriteTime", "File.SetCreationTime", "File.Encrypt",
        "FileAccess.Write", "FileAccess.ReadWrite", "FileMode.Create", "FileMode.Append", "FileMode.OpenOrCreate", "FileMode.Truncate", "FileMode.CreateNew",
        "Directory.Create", "Directory.Delete", "Directory.Move",
        "StreamWriter", "BinaryWriter", ".MoveTo(", ".CopyTo(", ".Delete(", "WriteAllText", "WriteAllBytes", "WriteAllLines",
        "OpenWrite", "AppendText", "CreateText", "MemoryMappedFile", "SafeFileHandle", "CreateFileW", "MoveFileEx", "ReplaceFile",
    };

    [Fact]
    public void Nothing_in_Data_or_Look_writes_a_file_except_SafeFile()
    {
        var hits = new List<string>();
        int files = 0;
        foreach (var sub in new[] { "Data", "Look" }) {
            string dir = SourceDir(sub);
            Assert.True(Directory.Exists(dir), "the Mantle.Core " + sub + " source was not found next to the tests (" + dir + "); this test reads it");
            foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)) {
                files++;
                if (sub == "Data" && Path.GetFileName(f) == "SafeFile.cs") continue;
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++) {
                    string code = lines[i];
                    int c = code.IndexOf("//", StringComparison.Ordinal);
                    if (c >= 0) code = code[..c];
                    foreach (var w in Forbidden)
                        if (code.Contains(w, StringComparison.Ordinal)) hits.Add($"{sub}/{Path.GetFileName(f)}:{i + 1}: {w}");
                }
            }
        }
        Assert.True(hits.Count == 0, "writing calls that skip SafeFile (and so the read-only gate): " + string.Join("; ", hits));
        Assert.True(files >= 10, "expected the data and look sources, found " + files + " files");
    }

    [Fact]
    public void Every_SafeFile_method_does_nothing_when_the_gate_says_no()
    {
        // a new public method on SafeFile must be added here, so it is proved gated
        var methods = typeof(SafeFile).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "MoveAside", "Remove", "WriteAtomic" }, methods);

        using var d = new TestDir("gate");
        d.Write("theme.json", "{\"mode\":\"dark\"}");
        var before = Snapshot(d.Path);
        var f = new SafeFile(WriteGate.Never("closed for the test"));
        Assert.Equal("closed for the test", f.WriteAtomic(d.File("theme.json"), new byte[] { 1 }, d.File("theme.json.tmp")));
        Assert.Equal("closed for the test", f.WriteAtomic(d.File(Path.Combine("new", "x.json")), new byte[] { 1 }, d.File(Path.Combine("new", "x.json.tmp"))));
        Assert.Equal("closed for the test", f.MoveAside(d.File("theme.json"), d.File("theme.json.unreadable")));
        Assert.Equal("closed for the test", f.Remove(d.File("theme.json")));
        var after = Snapshot(d.Path);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var k in before.Keys) Assert.True(before[k] == after[k], "changed: " + k);

        // a scratch gate still refuses anything outside its own folder
        using var other = new TestDir("gate-other");
        var scratch = new SafeFile(WriteGate.Scratch(d.Path));
        Assert.NotNull(scratch.WriteAtomic(other.File("x.json"), new byte[] { 1 }, other.File("x.json.tmp")));
        Assert.False(File.Exists(other.File("x.json")));
        Assert.Null(scratch.WriteAtomic(d.File("ok.json"), new byte[] { 1 }, d.File("ok.json.tmp")));
    }

    static DataPaths UserPaths(string appData, params string[] argv)
        => DataPaths.Resolve(LaunchArgs.Parse(argv), k => k == "APPDATA" ? appData : null, Path.GetTempPath());

    [Fact]
    public void The_users_own_profile_is_written_only_by_a_live_run_that_is_the_only_shell()
    {
        using var d = new TestDir("user");
        string theme = Path.Combine(d.Path, "mantle", "theme.json");
        string look = Path.Combine(d.Path, "look-sync", "look.json");
        var none = LaunchArgs.Parse(Array.Empty<string>());
        var liveArgs = LaunchArgs.Parse(new[] { "--live" });
        var live = UserPaths(d.Path, "--live");
        Assert.True(live.IsUserProfile);
        Assert.True(live.IsUserLook);

        var plain = UserPaths(d.Path);
        Assert.Contains("not --live", WriteGate.For(plain, none, () => null).Refusal(theme));
        Assert.Contains("not --live", WriteGate.For(plain, none, () => null).Refusal(look));
        Assert.Contains("Electron Mantle", WriteGate.For(live, liveArgs).Refusal(theme));   // not known = fail closed
        Assert.Contains("Electron Mantle is running", WriteGate.For(live, liveArgs, () => "Electron Mantle is running").Refusal(look));
        Assert.Null(WriteGate.For(live, liveArgs, () => null).Refusal(theme));
        Assert.Null(WriteGate.For(live, liveArgs, () => null).Refusal(look));
        Assert.Contains("outside", WriteGate.For(live, liveArgs, () => null).Refusal(Path.Combine(d.Path, "elsewhere.json")));

        // --user-data or LOOK_SYNC_DIR pointed at the real folders still counts as the user's own
        var aimed = DataPaths.Resolve(LaunchArgs.Parse(new[] { "--user-data=" + Path.Combine(d.Path, "MANTLE") }),
            k => k == "APPDATA" ? d.Path : k == "LOOK_SYNC_DIR" ? Path.Combine(d.Path, "look-sync") : null, Path.GetTempPath());
        Assert.True(aimed.IsUserProfile);
        Assert.True(aimed.IsUserLook);
        Assert.NotNull(WriteGate.For(aimed, none, () => null).Refusal(theme));

        // a test profile is its own scratch space
        var test = UserPaths(d.Path, "--test", "--profile-name=gate");
        Assert.False(test.IsUserProfile);
        Assert.False(test.IsUserLook);
        Assert.Null(WriteGate.For(test, LaunchArgs.Parse(new[] { "--test", "--profile-name=gate" })).Refusal(Path.Combine(test.DataRoot, "theme.json")));
    }

    [Fact]
    public void Stores_on_the_users_profile_without_live_leave_every_file_as_it_was()
    {
        using var d = new TestDir("user-stores");
        string root = Path.Combine(d.Path, "mantle");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "theme.json"), "{\"mode\":\"dark\"}");
        File.WriteAllText(Path.Combine(root, "features.json"), "[broken");
        var before = Snapshot(d.Path);
        var paths = UserPaths(d.Path);
        using (var stores = Stores.For(paths, WriteGate.For(paths, LaunchArgs.Parse(Array.Empty<string>())), new ListLog())) {
            Assert.False(stores.Open("theme.json").SetAsync("mode", JsonValue.Create("light")).Result.Ok);
            Assert.False(stores.Open("features.json").SetAsync("clipboard", JsonValue.Create(true)).Result.Ok);
            Assert.False(stores.Open("features/clipboard.json").UpdateAsync(o => o["x"] = 1).Result.Ok);
        }
        var after = Snapshot(d.Path);
        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));
        foreach (var k in before.Keys) Assert.True(before[k] == after[k], "changed: " + k);
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
