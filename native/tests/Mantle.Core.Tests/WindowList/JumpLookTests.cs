using Mantle.Core.Features.JumpList;
using Xunit;

namespace Mantle.Core.Tests.WindowList;

// BAR-329 (ui/bar/jumplist.css): the light menu's rows and Helm's dark menu's.
public class JumpLookTests
{
    [Fact]
    public void Light_is_the_bars_own_menu_and_dark_is_Helms()
    {
        var l = JumpLook.For(false);
        Assert.Equal((8d, 4d, 28d, 9d, 6d), (l.PadX, l.PadY, l.MinH, l.Gap, l.BodyPad));
        Assert.Equal(("LgHover", "LgHover", "LgAcc", 1.5), (l.Hover, l.FocusFill, l.FocusRing, l.RingW));
        var d = JumpLook.For(true);
        Assert.Equal((10d, 5d, 30d, 10d, 4d), (d.PadX, d.PadY, d.MinH, d.Gap, d.BodyPad));
        // hover and keyboard focus = the press surface, no accent; focus adds a 1 px hairline
        Assert.Equal(("LgPress", "LgPress", "LgEdgeOut", 1d), (d.Hover, d.FocusFill, d.FocusRing, d.RingW));
        Assert.Equal(("LgDivider", 6d, "LgInk2"), (d.Divider, d.SepX, d.IconInk));
        Assert.Equal(("LgEdgeOut", 8d, "LgInk3"), (l.Divider, l.SepX, l.IconInk));
    }

    [Fact]
    public void Plain_rows_take_the_themes_ink_and_a_row_with_its_own_keeps_it()
    {
        Assert.Equal("LgInk2", JumpLook.Light.RowInk("LgInk2"));
        Assert.Equal("LgInk", JumpLook.Dark.RowInk("LgInk2"));
        Assert.Equal("LgInk", JumpLook.Light.RowInk("LgInk"));
        Assert.Equal("LgWarn", JumpLook.Dark.RowInk("LgWarn"));
    }

    [Fact]
    public void The_look_is_said_in_words()
    {
        Assert.Equal("dark (Helm's menu): rows min 30 at 5 / 10, gap 10, hover LgPress, keyboard focus LgPress with a 1 px LgEdgeOut ring", JumpLook.Dark.Describe(true));
        Assert.Equal("light: rows min 28 at 4 / 8, gap 9, hover LgHover, keyboard focus LgHover with a 1.5 px LgAcc ring", JumpLook.Light.Describe(false));
    }
}
