using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Keys;
using Mantle.Core.Tests.Features;
using Xunit;

namespace Mantle.Core.Tests.Keys;

// main.js registerShortcuts (globalShortcut = RegisterHotKey) and the kill switch's road.
public class Win32HotkeysTests
{
    [Fact]
    public void Accelerators_InElectronsWords_BecomeModifiersAndAKey()
    {
        Assert.Equal(new Accelerator(Accelerator.Ctrl | Accelerator.Alt | Accelerator.Shift, 0x08), Accelerator.Parse(ShellShortcuts.KillSwitch, out _));
        Assert.Equal(new Accelerator(Accelerator.Alt, 0x20), Accelerator.Parse(ShellShortcuts.Launcher, out _));
        Assert.Equal(new Accelerator(Accelerator.Win, 'I'), Accelerator.Parse("Super+i", out _));
        Assert.Equal(new Accelerator(Accelerator.Ctrl | Accelerator.Shift, 0x87), Accelerator.Parse("CommandOrControl+Shift+F24", out _));
        Assert.Null(Accelerator.Parse("Hyper+Space", out var e1));
        Assert.Equal("\"Hyper\" in \"Hyper+Space\" is not a modifier (Ctrl, Alt, Shift, Win)", e1);
        Assert.Null(Accelerator.Parse("Alt+", out var e2));
        Assert.Equal("\"Alt+\" names no key", e2);
        Assert.Null(Accelerator.Parse("Alt+Banana", out var e3));
        Assert.Contains("is not a key this can register", e3);
    }

    [Fact]
    public void TheKillSwitch_StandsDownFirst_ThenRestores_ThenQuits_EvenWhenTheRestoreFails_AndASecondPressWaits() => HostLoop.Run(async (loop, t) =>
    {
        var steps = new List<string>();
        var restore = new TaskCompletionSource();
        var kill = ShellShortcuts.KillSwitchRoad(w => steps.Add($"standDown:{w}"), w => { steps.Add($"restore:{w}"); return restore.Task; }, w => steps.Add($"quit:{w}"), steps.Add);
        kill();
        kill();
        Assert.Equal("standDown:the kill switch", steps[1]);
        Assert.Equal("restore:the kill switch", steps[2]);
        Assert.Contains("Ctrl+Alt+Shift+Backspace: already putting Windows back", steps);
        Assert.DoesNotContain("quit:the kill switch", steps);   // not before the restore is done
        restore.SetException(new InvalidOperationException("the taskbar did not come back"));
        await Task.Delay(1);
        loop.Drain();
        Assert.Contains("Ctrl+Alt+Shift+Backspace: the restore failed (the taskbar did not come back); quitting anyway", steps);
        Assert.Equal("quit:the kill switch", steps[^1]);

        var order = new List<string>();
        ShellShortcuts.KillSwitchRoad(_ => throw new Exception("no supervisor"), _ => { order.Add("restore"); return Task.CompletedTask; }, _ => order.Add("quit"), order.Add)();
        await Task.Delay(1);
        loop.Drain();
        Assert.Equal(new[] { "restore", "quit" }, order.FindAll(s => s is "restore" or "quit"));
    });

    // ------------------------------------------------------------------ real RegisterHotKey (CI runners only)
    const string Odd = "Ctrl+Alt+Shift+F24";   // nobody's shortcut

    [CiFact]
    public void ARealShortcut_Registers_ASecondOwnerIsRefusedAsTaken_AndLettingGoFreesIt()
    {
        var posted = new BlockingCollection<Action>();
        using var a = new Win32Hotkeys(posted.Add);
        using var b = new Win32Hotkeys(posted.Add);
        Assert.True(a.Register(Odd, () => { }));
        Assert.Equal(new[] { Odd }, a.Registered);
        Assert.False(b.Register(Odd, () => { }));   // taken (another app, as Windows sees it)
        b.UnregisterAll();
        a.UnregisterAll();
        Assert.Empty(a.Registered);
        Assert.True(b.Register(Odd, () => { }));
        Assert.Throws<ArgumentException>(() => b.Register("Alt+Banana", () => { }));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; public ulong pad; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

    static INPUT Key(ushort vk, bool up) => new() { type = 1, ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? 2u : 0u } };

    [CiFact]
    public void APressedShortcut_RunsItsHandler_OnTheHost_NotOnTheShortcutThread()
    {
        var posted = new BlockingCollection<Action>();
        using var keys = new Win32Hotkeys(posted.Add);
        var hits = 0;
        var thread = -1;
        Assert.True(keys.Register(Odd, () => { hits++; thread = Environment.CurrentManagedThreadId; }));
        var seq = new[] { Key(0x11, false), Key(0x12, false), Key(0x10, false), Key(0x87, false), Key(0x87, true), Key(0x10, true), Key(0x12, true), Key(0x11, true) };
        Assert.Equal((uint)seq.Length, SendInput((uint)seq.Length, seq, Marshal.SizeOf<INPUT>()));
        Assert.True(posted.TryTake(out var run, 3000), "WM_HOTKEY never arrived for the injected Ctrl+Alt+Shift+F24");
        run();
        Assert.Equal(1, hits);
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
    }
}
