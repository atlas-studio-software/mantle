using System;
using System.IO;
using Mantle.Core.Data;
using Mantle.Core.Features.Clipboard;
using Xunit;

namespace Mantle.Core.Tests.ClipEmoji;

/// <summary>
/// The host side of clipboard history (CLIP-005, 019, 040, 044): where the pins live and when the sidecar may have the
/// file, clip.start's request, health words, item ids and the list cache. Paths are TestDir folders under %TEMP%.
/// </summary>
public sealed class ClipHostTests
{
    // SAFETY (CLIP-040 storage, never write the Electron app's files): the sidecar gets the pins path only where native may write
    [Fact]
    public void The_pins_file_is_handed_to_the_sidecar_only_where_native_may_write_it()
    {
        using var d = new TestDir("clip-pins");
        var mine = ClipHost.PinsFor(d.Path, WriteGate.Scratch(d.Path));
        Assert.Equal(Path.Combine(d.Path, "features", "clipboard-pins.dat"), mine.PinsFile);
        Assert.Contains(@"features\clipboard-pins.dat", mine.Why);
        var start = ClipHost.StartRequest(mine, test: true);
        Assert.Equal(mine.PinsFile, (string?)start["pinsFile"]);
        Assert.False((bool)start["listen"]!);                         // a test instance never listens to the user's copies

        var closed = ClipHost.PinsFor(d.Path, WriteGate.Never("Electron Mantle is running"));
        Assert.Null(closed.PinsFile);
        Assert.Contains("Electron Mantle is running", closed.Why);
        Assert.Contains("for this run only", closed.Why);
        var req = ClipHost.StartRequest(closed, test: false);
        Assert.False(req.ContainsKey("pinsFile"));                    // no path: the sidecar cannot write Electron's file
        Assert.True((bool)req["listen"]!);

        using var other = new TestDir("clip-pins-other");               // a gate for another folder refuses this one
        Assert.Null(ClipHost.PinsFor(d.Path, WriteGate.Scratch(other.Path)).PinsFile);
        Assert.Null(ClipHost.PinsFor("", WriteGate.Scratch(d.Path)).PinsFile);
        Assert.Empty(Directory.GetFileSystemEntries(d.Path));          // deciding wrote nothing
    }

    [Fact]
    public void Start_answers_become_the_health_words_electron_uses()
    {
        Assert.Equal("the sidecar is too old for clipboard history (it has no clip.start)", ClipHost.StartWhy(false, "unknown command clip.start", false, null, null));
        Assert.Equal("the sidecar is too old for clipboard history (it has no clip.start)", ClipHost.StartWhy(false, "No such command", false, null, null));
        Assert.Equal("clipboard history could not start: timed out", ClipHost.StartWhy(false, "timed out", false, null, null));
        Assert.Equal("not recording copies: the listener is off", ClipHost.StartWhy(true, null, false, "off", null));
        Assert.Equal("not recording copies: AddClipboardFormatListener failed", ClipHost.StartWhy(true, null, false, "off", "AddClipboardFormatListener failed"));
        Assert.Null(ClipHost.StartWhy(true, null, false, "history", null));
        Assert.Null(ClipHost.StartWhy(true, null, true, "off", null));    // a test instance does not listen
        Assert.Equal((true, (string?)null), ClipHost.Health(null, null));
        Assert.Equal((false, (string?)"the clipboard panel stopped responding"), ClipHost.Health("the clipboard panel stopped responding", "not recording copies: x"));
        Assert.Equal((false, (string?)"not recording copies: x"), ClipHost.Health(null, "not recording copies: x"));
    }

    [Theory]
    [InlineData("c1", true)] [InlineData("c12345", true)] [InlineData("", false)] [InlineData(null, false)] [InlineData("c", false)]
    [InlineData("x1", false)] [InlineData("c1\n", false)] [InlineData("c1 ", false)] [InlineData("C1", false)]
    public void Item_ids_are_checked_before_they_reach_the_sidecar(string? id, bool ok)
    {
        if (ok) Assert.Equal(id, ClipHost.ItemArg(id));
        else Assert.Equal("no item was chosen", Assert.Throws<ArgumentException>(() => ClipHost.ItemArg(id)).Message);
    }

    [Fact]
    public void The_list_cache_is_used_only_while_nothing_changed_since_it_was_read()
    {
        var c = new ClipListCache<string>();
        Assert.Null(c.Current(false));                     // nothing read yet: the open asks the sidecar
        int v = c.BeginRead();
        c.EndRead(v, "list 1");
        Assert.Equal("list 1", c.Current(false));          // CLIP-005: warm open, no clip.list on its path
        Assert.Null(c.Current(true));                      // never in a test instance

        c.Invalidate();                                    // a copy, pin or delete
        Assert.Null(c.Current(false));

        int racing = c.BeginRead();
        c.Invalidate();                                    // a copy while the read was out
        c.EndRead(racing, "stale");
        Assert.Null(c.Current(false));                     // a read that raced a change is not kept
        c.EndRead(c.BeginRead(), "list 2");
        Assert.Equal("list 2", c.Current(false));
        Assert.Equal(400, ClipHost.RelistMs);
        Assert.Equal(4000, ClipHost.WarmMs);
    }
}
