using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Mantle.Core.Features.Toasts;
using Microsoft.Win32;
using Xunit;

namespace Mantle.Core.Tests.Features.Toasts;

// THE CRASH STORY, end to end on a real registry (TST-078, TST-082, TST-087, TST-091, TST-099): the native owner
// takes test apps in a SCRATCH key (HKCU\Software\Mantle-test-<guid>\Settings, never Windows' own), writing its
// record through the real file store and the write gate; then Mantle "dies" without putting anything back, and
// tools/restore-banners.ps1 (what the crash supervisor's give-up and the safety grace run) must read the native
// record and put every original back, leave the user's own 0 alone, and write its marker so the next Mantle
// leaves a banner the user turns off afterwards as he set it. The scratch key is deleted at the end.
// Windows only (the CI runner); on anything else the tests return at once.
public sealed class BannerCrashRestoreTests : IDisposable
{
    const string A = "Mantle.ToastTest.A", B = "Mantle.ToastTest.B", C = "Mantle.ToastTest.C";
    readonly string _root = @"Software\Mantle-test-" + Guid.NewGuid().ToString("N");
    string Key => _root + @"\Settings";
    readonly TestDir _dir = new("banners");

    /// toasts.banners against the scratch key, with the sidecar's own semantics (sidecar/ToastBanners.cs).
    sealed class ScratchReg : IBannerRegistry
    {
        readonly string _key;
        public ScratchReg(string key)
        {
            if (!key.StartsWith(@"Software\Mantle-test-", StringComparison.Ordinal)) throw new InvalidOperationException("only a scratch key");
            _key = key;
        }
        static int? Dword(RegistryKey k, out bool odd) { odd = false; var v = k.GetValue("ShowBanner"); if (v == null) return null; if (v is int i) return i; odd = true; return null; }
        public Task<BannerRead?> ReadAsync()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var root = Registry.CurrentUser.OpenSubKey(_key)!;
            var apps = root.GetSubKeyNames().Select(n =>
            {
                using var k = root.OpenSubKey(n)!;
                var b = Dword(k, out bool odd);
                return new BannerApp(n, b, k.GetValue("Enabled") as int?, odd);
            }).ToArray();
            return Task.FromResult<BannerRead?>(new BannerRead(true, null, apps));
        }
        public Task<BannerWrite?> WriteAsync(IReadOnlyList<BannerSet> set)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var root = Registry.CurrentUser.OpenSubKey(_key, true)!;
            var res = set.Select(s =>
            {
                if (!s.Aumid.StartsWith(BannerOwner.TestPrefix, StringComparison.OrdinalIgnoreCase)) return new BannerWriteResult(s.Aumid, false, null, "not a test id");
                using var k = root.OpenSubKey(s.Aumid, true)!;
                if (s.Value is int v) k.SetValue("ShowBanner", v, RegistryValueKind.DWord); else k.DeleteValue("ShowBanner", false);
                var now = Dword(k, out bool odd);
                return new BannerWriteResult(s.Aumid, !odd && now == s.Value, now, null);
            }).ToArray();
            return Task.FromResult<BannerWrite?>(new BannerWrite(res.All(x => x.Ok), null, res));
        }
    }

    void Make(string id, int? banner)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var k = Registry.CurrentUser.CreateSubKey(Key + "\\" + id);
        if (banner is int b) k.SetValue("ShowBanner", b, RegistryValueKind.DWord);
    }

    object? Value(string id)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var k = Registry.CurrentUser.OpenSubKey(Key + "\\" + id);
        return k?.GetValue("ShowBanner");
    }

    static string Script()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var f = Path.Combine(d.FullName, "tools", "restore-banners.ps1");
            if (File.Exists(f)) return f;
        }
        throw new FileNotFoundException("tools/restore-banners.ps1 was not found above the test folder");
    }

    public void Dispose()
    {
        if (OperatingSystem.IsWindows()) Registry.CurrentUser.DeleteSubKeyTree(_root, false);
        _dir.Dispose();
    }

    [Fact]
    public async Task A_crash_leaves_the_native_record_and_restore_banners_ps1_puts_every_original_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        Make(A, 1); Make(B, null); Make(C, 0);
        var store = new BannerRecordFile(_dir.Path, new SafeFile(WriteGate.Scratch(_dir.Path)), pid: 4242);
        var owner = new BannerOwner(new ScratchReg(Key), store, test: true);
        var r = await owner.ApplyAsync("start");
        Assert.True(r.Ok, r.Reason);
        Assert.Equal(new[] { A, B }, r.Took.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(0, Value(A)); Assert.Equal(0, Value(B)); Assert.Equal(0, Value(C));

        // the record on disk is Electron's shape: v, owned (1 / null), taken (integers), released, pid, at
        var rec = (JsonObject)JsonNode.Parse(File.ReadAllText(store.Path))!;
        Assert.Equal(1, (int)rec["v"]!);
        Assert.Equal(1, (int)rec["owned"]![A]!); Assert.Null(rec["owned"]![B]); Assert.False(((JsonObject)rec["owned"]!).ContainsKey(C));
        Assert.Equal(4242, (int)rec["pid"]!);
        long stampA = (long)rec["taken"]![A]!;

        // Mantle dies here: nothing put back. The supervisor's give-up / the grace run the script, not Mantle.
        var script = new RestoreBannersScript(Script(), store.Path, test: true, settingsKey: Key);
        var run = script.RunSync();
        Assert.True(run.Error == null, run.Error);
        var read = BannerSafety.ReadOut(run.Code, run.Out);
        Assert.True(read.Ok, run.Out);
        Assert.Equal(2, read.Back);
        Assert.Equal(1, Value(A));            // back to its original
        Assert.Null(Value(B));                // no value again (Windows' default, banners on)
        Assert.Equal(0, Value(C));            // the user's own 0, never Mantle's, untouched

        // the script never writes the record, and lists what it put back with the record's stamps
        Assert.True(File.Exists(store.Path));
        var marker = Path.Combine(_dir.Path, "notif-banners-back.script.json");
        Assert.True(File.Exists(marker), "the script wrote no marker");
        var back = BannerRecordFile.ReadBackFiles(_dir.Path);
        Assert.Empty(back.Unreadable);
        Assert.Contains(back.Entries, e => e.Aumid == A && e.Took.Same(Stamp.Of(stampA)));

        // the next Mantle: A was turned off by the user after the put-back -> his; B is back to its original -> retaken
        if (OperatingSystem.IsWindows()) using (var k = Registry.CurrentUser.OpenSubKey(Key + "\\" + A, true)!) k.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
        var next = new BannerOwner(new ScratchReg(Key), new BannerRecordFile(_dir.Path, new SafeFile(WriteGate.Scratch(_dir.Path))), test: true);
        var r2 = await next.ApplyAsync("Mantle started again");
        Assert.Contains(A, r2.Released); Assert.Contains(B, r2.Retook);
        Assert.False(next.Owns(A)); Assert.True(next.Owns(B));

        // a clean quit then puts B back and leaves A and C as the user set them
        var q = await next.RestoreAsync("quit", keepReleased: true);
        Assert.True(q.Ok, q.Reason);
        Assert.Null(Value(B)); Assert.Equal(0, Value(A)); Assert.Equal(0, Value(C));
    }

    [Fact]
    public async Task Leaving_while_the_helper_is_down_runs_the_script_to_its_end()
    {
        if (!OperatingSystem.IsWindows()) return;
        Make(A, 1);
        var store = new BannerRecordFile(_dir.Path, new SafeFile(WriteGate.Scratch(_dir.Path)));
        var owner = new BannerOwner(new ScratchReg(Key), store, test: true);
        await owner.ApplyAsync("start");
        Assert.Equal(0, Value(A));
        var clock = new FakeToastClock(1000);
        var script = new RestoreBannersScript(Script(), store.Path, test: true, settingsKey: Key);
        var logs = new List<string>();
        var safety = new BannerSafety(() => owner.Counts().Owned, script.RunAsync, script.RunSync, clock, logs.Add);
        safety.Down("the helper exited");
        Assert.True(safety.State().Pending);
        Assert.True(safety.Leave("Mantle quit"), string.Join(" | ", logs));
        Assert.Equal(1, Value(A));
        Assert.False(safety.State().Pending);
    }

    [Fact]
    public async Task A_record_the_gate_refuses_means_nothing_is_written_to_Windows()
    {
        if (!OperatingSystem.IsWindows()) return;
        Make(A, 1);
        var store = new BannerRecordFile(_dir.Path, new SafeFile(WriteGate.Never("a read-only run")));
        var r = await new BannerOwner(new ScratchReg(Key), store, test: true).ApplyAsync("start");
        Assert.False(r.Ok);
        Assert.Contains("changed nothing", r.Reason);
        Assert.Equal(1, Value(A));
        Assert.False(File.Exists(store.Path));
    }
}
