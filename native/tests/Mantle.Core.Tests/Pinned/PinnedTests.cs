using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Pinned;
using Mantle.Core.Features.WindowList;
using Xunit;

namespace Mantle.Core.Tests.Pinned;

// Ported from lib/pinned.test.js and lib/features/jumplist.test.js (the overlay's read side): BAR-262..276, BAR-280, BAR-282, BAR-284.
public class PinnedTests
{
    /// A minimal .lnk: LinkInfo with an ANSI local base path, and an icon location (MS-SHLLINK).
    internal static byte[] Lnk(string target, string? iconLocation = null, string? args = null)
    {
        var b = new List<byte>();
        uint flags = 0x02 | 0x80 | (iconLocation != null ? 0x40u : 0) | (args != null ? 0x20u : 0);
        var hdr = new byte[0x4C];
        BitConverter.GetBytes(0x4Cu).CopyTo(hdr, 0);
        new byte[] { 0x01, 0x14, 0x02, 0, 0, 0, 0, 0, 0xC0, 0, 0, 0, 0, 0, 0, 0x46 }.CopyTo(hdr, 4);
        BitConverter.GetBytes(flags).CopyTo(hdr, 0x14);
        b.AddRange(hdr);
        var basePath = Encoding.Latin1.GetBytes(target + "\0");
        int size = 0x1C + basePath.Length + 1;
        var li = new byte[size];
        BitConverter.GetBytes((uint)size).CopyTo(li, 0);
        BitConverter.GetBytes(0x1Cu).CopyTo(li, 4);
        BitConverter.GetBytes(1u).CopyTo(li, 8);
        BitConverter.GetBytes(0x1Cu).CopyTo(li, 0x10);
        BitConverter.GetBytes((uint)(0x1C + basePath.Length)).CopyTo(li, 0x18);
        basePath.CopyTo(li, 0x1C);
        b.AddRange(li);
        void Str(string s) { b.AddRange(BitConverter.GetBytes((ushort)s.Length)); b.AddRange(Encoding.Unicode.GetBytes(s)); }
        if (args != null) Str(args);
        if (iconLocation != null) Str(iconLocation);
        return b.ToArray();
    }

    [Fact]
    public void Lnk_parser_reads_the_target_icon_location_and_args()
    {
        var p = LnkFile.Parse(Lnk(@"C:\Apps\x.exe", @"C:\i.dll,-3", "--flag"));
        Assert.Equal(@"C:\Apps\x.exe", p.Target);
        Assert.Equal("--flag", p.Args);
        Assert.Equal(@"C:\i.dll", p.IconPath);
        Assert.Equal(-3, p.IconIndex);
    }

    [Fact]
    public void Lnk_parser_refuses_what_is_not_a_shortcut_in_plain_words()
    {
        Assert.Contains("too small", Assert.ThrowsAny<Exception>(() => LnkFile.Parse(new byte[10])).Message);
        var bad = Lnk(@"C:\x.exe"); bad[5] = 0x99;
        Assert.Contains("class id", Assert.ThrowsAny<Exception>(() => LnkFile.Parse(bad)).Message);
    }

    [Fact]
    public void Icon_location_splits_path_and_index()
    {
        Assert.Equal((@"C:\Windows\system32\shell32.dll", 15), PinnedReader.SplitIconLocation(@"C:\Windows\system32\shell32.dll,15"));
        Assert.Equal((@"C:\a.ico", 0), PinnedReader.SplitIconLocation(@"C:\a.ico"));
        Assert.Equal(((string?)null, 0), PinnedReader.SplitIconLocation("  "));
    }

    [Fact]
    public void Vars_expand_case_insensitively_and_unknown_ones_stay()
    {
        string? Env(string n) => n.Equals("SystemRoot", StringComparison.OrdinalIgnoreCase) ? @"C:\Windows" : null;
        Assert.Equal(@"C:\Windows\x.exe %NOPE%\y", PinnedReader.ExpandVars(@"%systemroot%\x.exe %NOPE%\y", Env));
    }

    [Fact]
    public void Own_apps_say_their_own_names()
    {
        Assert.Equal("Quartz", PinnedReader.PinName("Browser.lnk", @"C:\b\Quartz.exe"));
        Assert.Equal("Notepad", PinnedReader.PinName("Notepad.LNK", @"C:\Windows\notepad.exe"));
    }

    [Fact]
    public void Order_comes_from_the_blob_utf16_then_ansi_and_the_rest_go_last_alphabetically()
    {
        var blob = Encoding.Unicode.GetBytes("xx Zeta.lnk yy").Concat(Encoding.ASCII.GetBytes(" alpha.LNK ")).ToArray();
        var o = PinnedReader.OrderNames(new[] { "Alpha.lnk", "Beta.lnk", "Zeta.lnk", "Aaa.lnk" }, blob);
        Assert.Equal(0, o["Zeta.lnk"]);
        Assert.Equal(1, o["Alpha.lnk"]);
        Assert.Equal(2, o["Aaa.lnk"]);
        Assert.Equal(3, o["Beta.lnk"]);
        var none = PinnedReader.OrderNames(new[] { "b.lnk", "a.lnk" }, null);
        Assert.Equal(0, none["a.lnk"]);
    }

    [Fact]
    public async Task A_missing_pins_folder_is_the_only_hard_failure_and_says_no_path()
    {
        using var d = new TestDir("pins");
        var r = await PinnedReader.ListAsync(new PinnedOptions { PinsDir = Path.Combine(d.Path, "nope") });
        Assert.False(r.Ok);
        Assert.Equal("Windows' pinned-apps folder is missing", r.Reason);
    }

    [Fact]
    public async Task Nothing_pinned_is_ok_with_a_note()
    {
        using var d = new TestDir("pins");
        var r = await PinnedReader.ListAsync(new PinnedOptions { PinsDir = d.Path });
        Assert.True(r.Ok);
        Assert.Empty(r.Items);
        Assert.Contains("nothing is pinned to the taskbar", r.Reason);
    }

    [Fact]
    public async Task Shortcuts_without_the_sidecar_are_read_by_the_built_in_parser_and_a_gone_app_keeps_its_pin()
    {
        using var d = new TestDir("pins");
        string exe = d.Write("apps/real.exe", "x");
        Directory.CreateDirectory(d.File("TaskBar"));
        File.WriteAllBytes(d.File("TaskBar/Real.lnk"), Lnk(exe));
        File.WriteAllBytes(d.File("TaskBar/Gone.lnk"), Lnk(@"C:\definitely\not\here.exe"));
        File.WriteAllText(d.File("TaskBar/readme.txt"), "not a pin");
        var r = await PinnedReader.ListAsync(new PinnedOptions { PinsDir = d.File("TaskBar") });
        Assert.True(r.Ok);
        Assert.Equal(new[] { "Gone", "Real" }, r.Items.Select(p => p.Name));
        Assert.Contains("shortcuts read without the sidecar", r.Reason);
        Assert.Contains("taskbar order unavailable", r.Reason);
        var gone = r.Items[0];
        Assert.Null(gone.Exe);
        Assert.Equal("the app this pin points at is gone (it was uninstalled or moved)", gone.Reason);
        var real = r.Items[1];
        Assert.Equal(exe.ToLowerInvariant(), real.Exe);
        Assert.Equal(exe, real.IconPath);
        Assert.Equal(new[] { 0, 1 }, r.Items.Select(p => p.Order));
    }

    [Fact]
    public async Task The_sidecar_order_merges_store_pins_and_puts_unlisted_shortcuts_last()
    {
        using var d = new TestDir("pins");
        string exe = d.Write("apps/a.exe", "x");
        Directory.CreateDirectory(d.File("TaskBar"));
        File.WriteAllBytes(d.File("TaskBar/A.lnk"), Lnk(exe));
        File.WriteAllBytes(d.File("TaskBar/Extra.lnk"), Lnk(exe));
        var calls = new List<string>();
        SidecarCall call = (verb, args, _) => {
            calls.Add(verb);
            string json = verb switch {
                "pins" => "{\"ok\":true,\"skipped\":1,\"items\":[{\"index\":1,\"kind\":\"lnk\",\"name\":\"A\",\"lnk\":\"A.lnk\"},{\"index\":0,\"kind\":\"app\",\"name\":\"Calc\",\"aumid\":\"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App\",\"iconPng\":null},{\"index\":2,\"kind\":\"lnk\",\"name\":\"Lost\",\"lnk\":\"Lost.lnk\"}]}",
                "lnk" => "{\"items\":[]}",
                "app" => "{\"ok\":true,\"name\":\"Calculator\",\"iconPng\":\"data:image/png;base64,AAAA\"}",
                _ => "{}",
            };
            using var doc = JsonDocument.Parse(json);
            return ValueTask.FromResult(doc.RootElement.Clone());
        };
        var r = await PinnedReader.ListAsync(new PinnedOptions { PinsDir = d.File("TaskBar"), ReadFavorites = () => new byte[] { 1, 2, 3 }, Call = call });
        Assert.True(r.Ok);
        Assert.Equal(new[] { "Calculator", "A", "Extra" }, r.Items.Select(p => p.Name));
        Assert.Equal("app", r.Items[0].Kind);
        Assert.Equal("data:image/png;base64,AAAA", r.Items[0].IconPng);
        Assert.Equal(1, calls.Count(c => c == "app"));
        Assert.Contains("not in the saved taskbar list, shown last: Extra", r.Reason);
        Assert.Contains("pinned but no shortcut file on disk: Lost", r.Reason);
        Assert.Contains("1 taskbar entry is not apps", r.Reason);
    }

    // ---------------------------------------------------------------- the overlay (lib/features/_jumplist-pins.js)
    static Pin P(string id, string? exe, int order, string? aumid = null) => new() { Id = id, Name = id, Exe = exe, Aumid = aumid, Order = order };

    [Fact]
    public void Overlay_hides_adds_once_per_app_and_orders_by_id()
    {
        using var doc = JsonDocument.Parse("{\"hide\":[{\"id\":\"b.lnk\"}],\"add\":[{\"id\":\"m1\",\"name\":\"Mine\",\"exe\":\"C:\\\\M.exe\"},{\"id\":\"dup\",\"exe\":\"c:\\\\a.exe\"},{\"nope\":1}],\"order\":[\"m1\",\"a.lnk\",5,\"m1\"]}");
        var o = PinOverlay.Norm(doc.RootElement);
        Assert.Equal(new[] { "m1", "a.lnk" }, o.Order);
        var res = o.Apply(new[] { P("a.lnk", @"c:\a.exe", 0), P("b.lnk", @"c:\b.exe", 1) });
        Assert.Equal(new[] { "m1", "a.lnk" }, res.Select(p => p.Id));
        Assert.Equal("mantle", res[0].Kind);
        Assert.Equal(@"c:\m.exe", res[0].Exe);
    }

    [Fact]
    public void Two_aumids_on_one_exe_are_two_apps()
    {
        Assert.False(PinOverlay.SameApp("electron.exe", "Mantle.Files", "electron.exe", "Mantle.Settings"));
        Assert.True(PinOverlay.SameApp(@"C:\X.exe", null, @"c:\x.exe", "Y"));
    }

    [Fact]
    public void A_broken_overlay_file_is_a_plain_reason_and_a_missing_one_is_empty()
    {
        using var d = new TestDir("overlay");
        Assert.Same(PinOverlay.Empty, PinOverlay.Read(d.Path));
        d.Write(@"features\jumplist.json", "{not json");
        Assert.Contains("not valid JSON", Assert.Throws<InvalidOperationException>(() => PinOverlay.Read(d.Path)).Message);
        d.Write(@"features\jumplist.json", "{\"other\":1,\"pins\":{\"order\":[\"x\"]}}");
        Assert.Equal(new[] { "x" }, PinOverlay.Read(d.Path).Order);
    }

    [Fact]
    public void Running_order_sorts_running_items_in_their_own_slots_and_prunes_stopped_apps()
    {
        var ro = new RunningOrder();
        ro.Set(new[] { "r:c", "r:a", "r:gone" });
        var items = new[] { "p:1", "r:a", "r:b", "r:c" };
        var res = ro.Apply(items, s => s.StartsWith("r:"), s => s, prune: true);
        Assert.Equal(new[] { "p:1", "r:c", "r:a", "r:b" }, res);
        Assert.Equal(new[] { "r:c", "r:a" }, ro.Current);
    }
}
