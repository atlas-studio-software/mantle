using System.Linq;
using Mantle.Core.Features.QuickLinks;
using Xunit;

namespace Mantle.Core.Tests.QuickLinks;

/// <summary>lib/quick-links.js: Windows' Win+X rows, their letters and the submenu.</summary>
public class WinXLinksTests
{
    [Fact]
    public void The_rows_are_Windows_own_in_its_order()
    {
        Assert.Equal(new[] { "Installed apps", "Mobility Center", "Power Options", "Event Viewer", "System", "Device Manager", "Network Connections",
            "Disk Management", "Computer Management", "Terminal", "Terminal (Admin)", "Task Manager", "Settings", "File Explorer", "Search", "Run",
            "Shut down or sign out", "Desktop" }, WinXLinks.Links.Select(l => WinXLinks.Parse(l.Label).Text));
        Assert.Equal(new[] { "Sign out", "Sleep", "Hibernate", "Shut down", "Restart" }, WinXLinks.Power.Select(p => WinXLinks.Parse(p.Label).Text));
        Assert.Equal(new[] { "signout", "shutdown", "restart" }, WinXLinks.Power.Where(p => p.Confirm).Select(p => p.Id));
        // every letter is unique in its menu, so one key picks one row
        Assert.Equal(WinXLinks.Links.Count, WinXLinks.Links.Select(l => WinXLinks.Parse(l.Label).Key).Distinct().Count());
        Assert.Equal(WinXLinks.Power.Count, WinXLinks.Power.Select(p => WinXLinks.Parse(p.Label).Key).Distinct().Count());
        Assert.Equal(new[] { "eventvwr.msc", "devmgmt.msc", "diskmgmt.msc", "compmgmt.msc", "mblctr.exe" }.OrderBy(x => x),
            WinXLinks.Links.Where(l => l.Run?.Kind == "exec").Select(l => l.Run!.File!).OrderBy(x => x));
    }

    [Fact]
    public void A_label_splits_round_its_letter_and_a_key_picks_its_row_in_any_case()
    {
        Assert.Equal(new QuickLabel("Device Manager", "Device ", "M", "anager", "m"), WinXLinks.Parse("Device &Manager"));
        Assert.Equal(new QuickLabel("Plain", "Plain", "", "", ""), WinXLinks.Parse("Plain"));
        Assert.Equal(new QuickLabel("End&", "End&", "", "", ""), WinXLinks.Parse("End&"));
        Assert.Equal("devices", WinXLinks.ByKey("M")!.Id);
        Assert.Equal("devices", WinXLinks.ByKey("m")!.Id);
        Assert.Equal("terminaladmin", WinXLinks.ByKey("a")!.Id);
        Assert.Null(WinXLinks.ByKey("mm"));
        Assert.Null(WinXLinks.ByKey("z"));
        Assert.Equal("hibernate", WinXLinks.PowerByKey("H")!.Id);
        Assert.Equal("desktop", WinXLinks.ById("desktop")!.Id);
        Assert.Null(WinXLinks.ById("nope"));
    }
}
