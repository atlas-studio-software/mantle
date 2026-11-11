using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The menu commands (lib/features/topbar.js: about, settings, winSettings, taskmgr, newWindow) on the same rig, with a
/// fake shell: a --test run only says what it would have done; a live run opens and spawns through the shell.
/// </summary>
public sealed partial class TopBarFeatureTests
{
    sealed class FakeShell : ITopBarShell
    {
        public readonly List<string> Opened = new(), Spawned = new();
        public string? OpenError, SpawnError;
        public string? Open(string uri) { Opened.Add(uri); return OpenError; }
        public string? Spawn(string exe, IReadOnlyList<string> args) { Spawned.Add((exe + " " + string.Join(' ', args)).Trim()); return SpawnError; }
        public (string Version, string Release, string Cpu, double RamBytes) Machine() => ("0.1.1", "10.0.26100", "Intel(R) Core(TM)   i3-1215U  ", 11.7 * 1073741824);
    }

    static string Reason(JsonElement r) => r.TryGetProperty("reason", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static bool Flag(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
    static string[] Notes(Rig r) => r.Events.Published.Where(p => p.Name == "topbar.note").Select(p => ((TopBarNote)p.Payload!).Text ?? "").ToArray();

    [Fact]
    public void About_pushes_one_line_about_Mantle_and_the_machine_and_hello_drops_it_after_8_s_TOP_316() => T(async (r, s) =>
    {
        var sh = new FakeShell();
        await Up(r, s, shell: sh);
        var a = await r.Bus.CallAsync("topbar.about");
        const string want = "Mantle 0.1.1 · Windows 10.0.26100 · Intel(R) Core(TM) i3-1215U · 11.7 GB RAM";
        Assert.True(a.Ok);
        Assert.Equal(want, a.Result.GetProperty("text").GetString());
        Assert.Equal(want, Notes(r).Last());
        await Tick(r, 7000);
        Assert.Equal(want, (await r.Bus.CallAsync("topbar.hello")).Result.GetProperty("note").GetString());
        await Tick(r, 1200);
        Assert.Equal(JsonValueKind.Null, (await r.Bus.CallAsync("topbar.hello")).Result.GetProperty("note").ValueKind);
        Assert.Equal("Mantle ? · Windows 10.0.1 · AMD Ryzen 5 · 0.5 GB RAM", TopBarFeature.AboutText("?", "10.0.1", " AMD  Ryzen 5 ", 536870912));
    });

    [Fact]
    public void Settings_winSettings_and_taskmgr_are_dry_runs_in_test_and_open_or_run_live_TOP_317_318_319()
    {
        T(async (r, s) =>
        {
            var sh = new FakeShell();
            await Up(r, s, shell: sh);
            var st = await r.Bus.CallAsync("topbar.settings");
            Assert.True(Flag(st.Result, "dryRun"));
            // no feature owns Win+I on this rig, so the dry run says Windows' app would have opened, and why
            Assert.StartsWith("dry run (--test): would have opened Windows Settings (ms-settings:), because ", Reason(st.Result));
            var ws = await r.Bus.CallAsync("topbar.winSettings");
            Assert.Equal("dry run (--test): would have opened Windows Settings (ms-settings:)", Reason(ws.Result));
            var tm = await r.Bus.CallAsync("topbar.taskmgr");
            Assert.Matches(@"^dry run \(--test\): would have run .*\\System32\\Taskmgr\.exe$", Reason(tm.Result));
            Assert.Empty(sh.Opened); Assert.Empty(sh.Spawned);
        });
        T(async (r, s) =>
        {
            var sh = new FakeShell();
            await Up(r, s, shell: sh);
            var st = await r.Bus.CallAsync("topbar.settings");
            Assert.True(Flag(st.Result, "ok"));
            Assert.Equal("Windows Settings", st.Result.GetProperty("opened").GetString());
            Assert.StartsWith("Mantle Settings did not open (", Reason(st.Result));
            Assert.EndsWith("), so Windows Settings opened", Reason(st.Result));
            var ws = await r.Bus.CallAsync("topbar.winSettings");
            Assert.True(Flag(ws.Result, "ok"));
            Assert.Equal(new[] { "ms-settings:", "ms-settings:" }, sh.Opened);
            sh.OpenError = "no app is registered for it";
            ws = await r.Bus.CallAsync("topbar.winSettings");
            Assert.False(Flag(ws.Result, "ok"));
            Assert.Equal("Windows Settings did not open: no app is registered for it", Reason(ws.Result));
            var tm = await r.Bus.CallAsync("topbar.taskmgr");
            Assert.True(Flag(tm.Result, "ok"));
            Assert.EndsWith(@"\System32\Taskmgr.exe", sh.Spawned.Single());
        }, test: false);
    }

    [Fact]
    public void New_window_takes_only_the_known_args_of_an_app_on_disk_and_is_a_dry_run_in_test_TOP_314()
    {
        string dir = Path.Combine(Path.GetTempPath(), "mantle-tb-nw-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string code = Path.Combine(dir, "Code.exe"), np = Path.Combine(dir, "notepad.exe");
        File.WriteAllText(code, ""); File.WriteAllText(np, "");
        static JsonObject A(string exe, params string[] args) => new() { ["exe"] = exe, ["args"] = new JsonArray(args.Select(a => (JsonNode?)a).ToArray()) };
        try
        {
            T(async (r, s) =>
            {
                var sh = new FakeShell();
                await Up(r, s, shell: sh);
                Assert.Equal("Mantle does not know how to open a new Code.exe window", Reason((await r.Bus.CallAsync("topbar.newWindow", A(code))).Result));
                Assert.Equal("Mantle does not know how to open a new Code.exe window", Reason((await r.Bus.CallAsync("topbar.newWindow", A(code, "--evil"))).Result));
                Assert.Equal("Mantle does not know how to open a new app window", Reason((await r.Bus.CallAsync("topbar.newWindow", new JsonObject())).Result));
                string gone = Path.Combine(dir, "sub", "Code.exe");
                Assert.Equal(gone + " is not on disk any more", Reason((await r.Bus.CallAsync("topbar.newWindow", A(gone, "--new-window"))).Result));
                Assert.Equal("Code.exe is not on disk any more", Reason((await r.Bus.CallAsync("topbar.newWindow", A("Code.exe", "--new-window"))).Result));
                var ok = await r.Bus.CallAsync("topbar.newWindow", A(code, "--new-window"));
                Assert.True(Flag(ok.Result, "dryRun"));
                Assert.Equal($"dry run (--test): would have run {code} --new-window", Reason(ok.Result));
                Assert.Equal($"dry run (--test): would have run {np}", Reason((await r.Bus.CallAsync("topbar.newWindow", A(np))).Result));
                Assert.Empty(sh.Spawned);
            });
            T(async (r, s) =>
            {
                var sh = new FakeShell();
                await Up(r, s, shell: sh);
                Assert.True(Flag((await r.Bus.CallAsync("topbar.newWindow", A(code, "--new-window"))).Result, "ok"));
                Assert.Equal(code + " --new-window", sh.Spawned.Single());
                sh.SpawnError = "Access is denied";
                var no = await r.Bus.CallAsync("topbar.newWindow", A(np));
                Assert.False(Flag(no.Result, "ok"));
                Assert.Equal("New window: Access is denied", Notes(r).Last());
            }, test: false);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
