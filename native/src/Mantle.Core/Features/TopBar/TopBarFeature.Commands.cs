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

        ctx.Command("newWindow", c => new(NewWindow(c.Args)));
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
