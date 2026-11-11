using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mantle.Core.Commands;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.TopBar;

// lib/features/topbar.js's commands for the Mantle and app menus (TOP-314, TOP-316..319): about, settings, winSettings,
// taskmgr, newWindow. A --test run never opens or runs anything: each answers Electron's refuseInTest,
// {ok: true, dryRun: true, reason: "dry run (--test): would have <what>"}. Opening and spawning go through
// ITopBarShell, so the tests see what a live run would have done.

/// <summary>What the menu commands open and run, and the machine facts "About Mantle" names.</summary>
public interface ITopBarShell
{
    /// Opens a URI (ms-settings:) with the shell. Null when it opened, else why not.
    string? Open(string uri);
    /// Starts exe with args, detached (no handles, no wait). Null when it started, else why not.
    string? Spawn(string exe, IReadOnlyList<string> args);
    /// Mantle's version, Windows' release (os.release(): major.minor.build), the CPU model, the total RAM in bytes.
    (string Version, string Release, string Cpu, double RamBytes) Machine();
}

/// <summary>The real one.</summary>
public sealed class ProcessTopBarShell : ITopBarShell
{
    public string? Open(string uri)
    {
        try { using var p = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); return null; }
        catch (Exception e) { return e.Message; }
    }

    public string? Spawn(string exe, IReadOnlyList<string> args)
    {
        try
        {
            // UseShellExecute: no inherited handles (cp.spawn's detached + stdio 'ignore'); the window shows (windowsHide: false)
            var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return null;
        }
        catch (Exception e) { return e.Message; }
    }

    public (string Version, string Release, string Cpu, double RamBytes) Machine()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(ProcessTopBarShell).Assembly;
        string? v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (v != null) { int plus = v.IndexOf('+'); if (plus > 0) v = v[..plus]; }
        var os = Environment.OSVersion.Version;
        string cpu = "unknown CPU";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                if (k?.GetValue("ProcessorNameString") is string s && s.Trim().Length > 0) cpu = s;
            }
        }
        catch (Exception) { /* named as unknown */ }
        var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        double ram = GlobalMemoryStatusEx(ref m) ? m.TotalPhys : 0;
        return (string.IsNullOrEmpty(v) ? "?" : v, $"{os.Major}.{os.Minor}.{os.Build}", cpu, ram);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemStatus m);
}

public sealed partial class TopBarFeature
{
    readonly ITopBarShell _shell;
    string? _note;

    /// "Mantle 0.1.1 · Windows 10.0.26100 · Intel(R) Core(TM) i3-1215U · 11.7 GB RAM" (the CPU's runs of spaces made one).
    public static string AboutText(string version, string release, string cpu, double ramBytes)
        => $"Mantle {version} · Windows {release} · {string.Join(' ', cpu.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))} · " +
           (ramBytes / 1073741824).ToString("0.0", CultureInfo.InvariantCulture) + " GB RAM";

    /// A note on the strip (TOP-175): pushed to the surface, kept for hello, gone from hello after 8 s if unchanged.
    void Note(string text)
    {
        _note = text;
        Broadcast("topbar.note", new TopBarNote(text));
        Later(8000, () => { if (_note == text) _note = null; });
    }

    static CommandResult Refuse(string what)
        => CommandResult.Success(J(new JsonObject { ["ok"] = true, ["dryRun"] = true, ["reason"] = $"dry run (--test): would have {what}" }));

    void InstallMenuCommands(IFeatureContext ctx)
    {
        ctx.Replay("topbar.note", () => new TopBarNote(_note));

        ctx.Command("about", _ =>
        {
            var m = _shell.Machine();
            string text = AboutText(m.Version, m.Release, m.Cpu, m.RamBytes);
            Note(text);
            return Ok(new JsonObject { ["ok"] = true, ["text"] = text });
        });

        // Mantle Settings through Win+I's own handler; Windows' app when that cannot run, and the answer says why
        ctx.Command("settings", _ =>
        {
            var keys = ctx as IFeatureHostAccess;
            if (_test)
            {
                var d = keys?.RunChord("Win+I", "the Mantle menu", dryRun: true);
                return new(Refuse(d is { DryRun: true } ? "opened Mantle Settings (Win+I)"
                    : $"opened Windows Settings (ms-settings:), because {d?.Reason ?? "this host cannot run a chord"}"));
            }
            var r = keys?.RunChord("Win+I", "the Mantle menu");
            if (r is { Delivered: true }) return Ok(new JsonObject { ["ok"] = true, ["opened"] = "Mantle Settings" });
            return new(WinSettings($"Mantle Settings did not open ({r?.Reason ?? "this host cannot run a chord"}), so Windows Settings opened"));
        });

        ctx.Command("winSettings", _ => new(_test ? Refuse("opened Windows Settings (ms-settings:)") : WinSettings(null)));

        ctx.Command("taskmgr", _ =>
        {
            string exe = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32", "Taskmgr.exe");
            if (_test) return new(Refuse($"run {exe}"));
            string? err = _shell.Spawn(exe, Array.Empty<string>());
            return Ok(err == null ? new JsonObject { ["ok"] = true } : new JsonObject { ["ok"] = false, ["reason"] = err });
        });

        // TOP-101: a stat dropdown asks for its meter's details (top processes, cores, adapters); the sidecar sends them
        // for 10 s after each ask. The made-up sample already carries them, so a fixture run asks nothing.
        ctx.Command("statsDetail", async c =>
        {
            var kind = Str(c.Args, "kind");
            if (!TopBarStatMenu.IsKind(kind)) return NoWith("there are no details for that reading");
            if (TopBarStatMenu.Refusal(_stepAside, _peekShown, _kind) is { } no) return NoWith(no);
            if (_fixture != null) return Answer(new JsonObject { ["ok"] = true, ["fixture"] = true });
            var r = await _ctx.Platform.CallAsync("topbar.stats", new JsonObject { ["on"] = true, ["detail"] = kind }, TimeSpan.FromMilliseconds(1500));
            return r.Ok ? Answer(new JsonObject { ["ok"] = true }) : NoWith($"the details were not asked for: {r.Error}");
        });

        ctx.Command("newWindow", c => new(NewWindow(c.Args)));
        // the dropdowns' commands (TOP-061/064/065/054..058, TOP-046..049): every handle the surface names is checked
        // first (CheckArgs), so a menu command, a window action or a UI Automation click never reaches another window
        ctx.Command("menus", async c => await Menus(c.Args));
        ctx.Command("invoke", async c => await Invoke(c.Args));
        ctx.Command("uiaInvoke", async c => await UiaInvoke(c.Args));
        ctx.Command("window", async c => await WindowAction(c.Args));
        ctx.Command("power", async c => await Power(c.Args));
    }

    // ------------------------------------------------------------------ what the surface may ask for (TOP-308)
    public static readonly string[] WindowActions = { "minimize", "zoom", "ontop", "close", "quit" };
    const int MaxDepth = 8, MaxPos = 1000, MaxName = 260;

    /// The handle as the sidecar's decimal string, or null when it is not a window handle at all.
    public static string? HwndArg(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) && n > 0 && n <= 9007199254740991 ? n.ToString(CultureInfo.InvariantCulture) : null;
        return v.ValueKind == JsonValueKind.String && System.Text.RegularExpressions.Regex.IsMatch(v.GetString()!, @"^[1-9][0-9]{0,19}$") ? v.GetString() : null;
    }
    static int[]? PosList(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() is < 1 or > MaxDepth) return null;
        var o = new List<int>();
        foreach (var x in v.EnumerateArray()) { if (x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var p) || p < 0 || p >= MaxPos) return null; o.Add(p); }
        return o.ToArray();
    }

    /// lib/features/topbar.js checkArgs for the menu verbs: only the checked fields go on, or a reason in plain words.
    public static (JsonObject? Args, string? Reason) CheckArgs(string verb, JsonElement a, IReadOnlySet<string> allowed)
    {
        static (JsonObject?, string?) No(string why) => (null, why);
        string? hwnd = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("hwnd", out var hv) ? HwndArg(hv) : null;
        if (hwnd == null) return No("the app's window could not be identified, so nothing was sent to it");
        if (!allowed.Contains(hwnd)) return No("that window is no longer the app in front, so nothing was sent to it; open the menu again");
        JsonElement P(string k) => a.TryGetProperty(k, out var v) ? v : default;
        switch (verb)
        {
            case "menus":
                if (P("init").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return (new JsonObject { ["hwnd"] = hwnd }, null);
                return PosList(P("init")) is { } init ? (new JsonObject { ["hwnd"] = hwnd, ["init"] = Arr(init) }, null) : No("that menu could not be identified; open it again");
            case "invoke":
                return P("item") is { ValueKind: JsonValueKind.Number } it && it.TryGetInt32(out var id) && id is >= 0 and <= 0xFFFF
                    ? (new JsonObject { ["hwnd"] = hwnd, ["item"] = id }, null) : No("that menu item could not be identified; open the menu again");
            case "uiaInvoke":
            {
                var path = PosList(P("path"));
                if (path == null) return No("that menu item could not be identified; open the menu again");
                var names = new JsonArray();
                var nv = P("names");
                if (nv.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    if (nv.ValueKind != JsonValueKind.Array || nv.GetArrayLength() > path.Length) return No("that menu item could not be identified; open the menu again");
                    foreach (var n in nv.EnumerateArray())
                    {
                        if (n.ValueKind == JsonValueKind.Null) { names.Add((JsonNode?)null); continue; }
                        if (n.ValueKind != JsonValueKind.String || n.GetString()!.Length > MaxName) return No("that menu item could not be identified; open the menu again");
                        names.Add((JsonNode)JsonValue.Create(n.GetString()!)!);
                    }
                }
                return (new JsonObject { ["hwnd"] = hwnd, ["path"] = Arr(path), ["names"] = names, ["expand"] = P("expand").ValueKind == JsonValueKind.True }, null);
            }
            case "window":
            {
                var act = P("action").ValueKind == JsonValueKind.String ? P("action").GetString() : null;
                if (act == null || Array.IndexOf(WindowActions, act) < 0) return No("Mantle does not know that window action");
                var on = P("on");
                if (on.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False)) return No("Mantle does not know that window action");
                var o = new JsonObject { ["hwnd"] = hwnd, ["action"] = act };
                if (on.ValueKind is JsonValueKind.True or JsonValueKind.False) o["on"] = on.ValueKind == JsonValueKind.True;
                return (o, null);
            }
        }
        return No("Mantle does not know that request");
    }
    static JsonArray Arr(int[] a) => new(a.Select(x => (JsonNode)JsonValue.Create(x)).ToArray());

    /// Every handle the surface was handed: the app in front and its menu's owner.
    HashSet<string> PageHwnds()
    {
        var s = new HashSet<string>(StringComparer.Ordinal);
        void Add(JsonNode? n) { if (n is JsonValue v && HwndArg(JsonDocument.Parse(v.ToJsonString()).RootElement) is { } h) s.Add(h); }
        Add(_focus?["hwnd"]); Add((_focus?["menu"] as JsonObject)?["hwnd"]);
        return s;
    }
    (JsonObject? Args, string? Reason) Checked(string verb, JsonElement a)
    {
        var c = CheckArgs(verb, a, PageHwnds());
        if (c.Reason != null) _ctx.Log($"refused a page request ({verb}): {c.Reason}");
        return c;
    }
    static CommandResult NoWith(string why) => CommandResult.Success(J(new JsonObject { ["ok"] = false, ["reason"] = why }));
    static CommandResult FromSidecar(PlatformReply r)
        => r.Ok ? CommandResult.Success(r.Result) : NoWith(r.Error ?? "the Mantle helper gave no reason");

    /// TOP-061/067: the app's menu again (WM_INITMENUPOPUP for init, live only). A --test fixture answers its own menu.
    async System.Threading.Tasks.Task<CommandResult> Menus(JsonElement a)
    {
        var c = Checked("menus", a);
        if (c.Args == null) return NoWith(c.Reason!);
        if (_fixture != null) return CommandResult.Success(J(new JsonObject { ["menu"] = _focus?["menu"]?.DeepClone(), ["inited"] = 1 }));
        if (_test) c.Args.Remove("init");   // a --test run sends no message into a real app
        return FromSidecar(await _ctx.Platform.CallAsync("topbar.menus", c.Args));
    }

    /// TOP-064: a Win32 menu item (WM_COMMAND). A --test run only says what it would have sent.
    async System.Threading.Tasks.Task<CommandResult> Invoke(JsonElement a)
    {
        var c = Checked("invoke", a);
        if (c.Args == null) return NoWith(c.Reason!);
        if (_test) return Refuse($"sent menu item {c.Args["item"]} to window {c.Args["hwnd"]}");
        c.Args["focus"] = _live;
        return FromSidecar(await _ctx.Platform.CallAsync("topbar.invoke", c.Args));
    }

    /// TOP-065/066: a UI Automation menu item. Never in --test (one activated a test window and took the keyboard).
    async System.Threading.Tasks.Task<CommandResult> UiaInvoke(JsonElement a)
    {
        if (_test) return NoWith("a --test instance never runs a UI Automation action (one activated a test window and took the keyboard, 2026-09-24)");
        var c = Checked("uiaInvoke", a);
        if (c.Args == null) return NoWith(c.Reason!);
        return FromSidecar(await _ctx.Platform.CallAsync("topbar.uiaInvoke", c.Args, TimeSpan.FromSeconds(15)));
    }

    /// TOP-054..058: minimize, zoom, always on top, close, quit. A --test run only says what it would have done.
    async System.Threading.Tasks.Task<CommandResult> WindowAction(JsonElement a)
    {
        var c = Checked("window", a);
        if (c.Args == null) return NoWith(c.Reason!);
        if (_test) return Refuse($"run the window action {c.Args["action"]} on window {c.Args["hwnd"]}");
        return FromSidecar(await _ctx.Platform.CallAsync("topbar.window", c.Args));
    }

    /// launcher-index.js power (TOP-046..049): Lock, Sleep, Restart (asks first), Shut down (asks first).
    public static (string Name, bool Confirm, string? Exe, string[] Args)? PowerDef(string? action) => action switch
    {
        "lock" => ("Lock", false, "rundll32.exe", new[] { "user32.dll,LockWorkStation" }),
        "sleep" => ("Sleep", false, null, Array.Empty<string>()),
        "restart" => ("Restart", true, "shutdown.exe", new[] { "/r", "/t", "0" }),
        "shutdown" => ("Shut down", true, "shutdown.exe", LauncherIndex.PowerRows.ShutdownArgs(null)),
        _ => null,
    };

    async System.Threading.Tasks.Task<CommandResult> Power(JsonElement a)
    {
        var action = Str(a, "action");
        if (PowerDef(action) is not { } def) return NoWith($"there is no power action called \"{action}\"");
        if (def.Confirm && !Bool(a, "confirmed")) return NoWith(def.Name + " asks first: press Enter on it twice");
        string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (def.Exe == null)
        {
            if (_test) return Refuse("put the PC to sleep (the Mantle helper's session sleep)");
            var r = await _ctx.Platform.CallAsync("session", new JsonObject { ["action"] = action, ["dryRun"] = false }, TimeSpan.FromSeconds(4));
            if (!r.Ok) return NoWith(def.Name + " did not happen: " + (r.Error ?? "the Mantle helper gave no reason"));
            return CommandResult.Success(J(new JsonObject { ["ok"] = true }));
        }
        string exe = Path.Combine(sys32, def.Exe);
        if (_test) return Refuse($"run {exe} {string.Join(' ', def.Args)}");
        string? err = _shell.Spawn(exe, def.Args);
        return err == null ? CommandResult.Success(J(new JsonObject { ["ok"] = true })) : NoWith(def.Name + " did not happen: " + err);
    }

    CommandResult WinSettings(string? why)
    {
        string? err = _shell.Open("ms-settings:");
        return CommandResult.Success(J(err == null
            ? new JsonObject { ["ok"] = true, ["opened"] = "Windows Settings", ["reason"] = why }
            : new JsonObject { ["ok"] = false, ["reason"] = $"Windows Settings did not open: {err}" }));
    }

    CommandResult NewWindow(JsonElement a)
    {
        string exe = Str(a, "exe") ?? "";
        var want = TopBarModel.NewWindowArgs(exe);
        var args = new List<string>();
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("args", out var av) && av.ValueKind == JsonValueKind.Array)
            foreach (var x in av.EnumerateArray()) args.Add(x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText());
        JsonObject No(string why) => new() { ["ok"] = false, ["reason"] = why };
        if (want == null || !want.SequenceEqual(args))
        {
            string name = Path.GetFileName(exe);
            return CommandResult.Success(J(No($"Mantle does not know how to open a new {(name.Length > 0 ? name : "app")} window")));
        }
        if (!Path.IsPathFullyQualified(exe) || !File.Exists(exe)) return CommandResult.Success(J(No($"{(exe.Length > 0 ? exe : "the app")} is not on disk any more")));
        if (_test) return Refuse($"run {exe} {string.Join(' ', args)}".Trim());
        string? err = _shell.Spawn(exe, args);
        if (err != null) { Note($"New window: {err}"); return CommandResult.Success(J(No(err))); }
        return CommandResult.Success(J(new JsonObject { ["ok"] = true }));
    }
}
