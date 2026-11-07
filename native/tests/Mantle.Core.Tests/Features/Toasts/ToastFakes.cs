using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Toasts;

namespace Mantle.Core.Tests.Features.Toasts;

/// A manual clock: timers fire in time order as the test advances it (osd-model.test.js's clock()).
public sealed class FakeToastClock : IToastClock
{
    sealed class T : IDisposable { public double At; public Action A = null!; public bool Gone; public void Dispose() => Gone = true; }
    readonly List<T> _timers = new();
    public double NowMs { get; set; }
    public FakeToastClock(double start = 0) { NowMs = start; }
    public IDisposable Later(int ms, Action action) { var t = new T { At = NowMs + ms, A = action }; _timers.Add(t); return t; }
    public int Pending => _timers.Count(t => !t.Gone);
    public void Advance(double ms)
    {
        double end = NowMs + ms;
        while (true)
        {
            var next = _timers.Where(t => !t.Gone && t.At <= end).OrderBy(t => t.At).FirstOrDefault();
            if (next == null) break;
            next.Gone = true; NowMs = next.At; next.A();
        }
        NowMs = end;
    }
}

/// Windows' Notifications\Settings, one entry per app; records every write (the test's fakeReg).
public sealed class FakeReg : IBannerRegistry
{
    public sealed class App { public int? Banner; public int? Enabled; public bool Odd; }
    public readonly Dictionary<string, App> Apps = new(StringComparer.Ordinal);
    public readonly List<BannerSet> Writes = new();
    public readonly HashSet<string> FailFor = new(), StickFor = new();
    public bool ReadFails;
    public Action<IReadOnlyList<BannerSet>>? OnWrite;

    public FakeReg Add(string aumid, int? banner, int? enabled = null, bool odd = false) { Apps[aumid] = new App { Banner = banner, Enabled = enabled, Odd = odd }; return this; }

    public Task<BannerRead?> ReadAsync() => Task.FromResult<BannerRead?>(ReadFails
        ? new BannerRead(false, "the registry said no", Array.Empty<BannerApp>())
        : new BannerRead(true, null, Apps.Select(kv => new BannerApp(kv.Key, kv.Value.Banner, kv.Value.Enabled, kv.Value.Odd)).ToArray()));

    public Task<BannerWrite?> WriteAsync(IReadOnlyList<BannerSet> set)
    {
        OnWrite?.Invoke(set);
        var results = set.Select(s =>
        {
            Writes.Add(s);
            if (!Apps.TryGetValue(s.Aumid, out var a)) return new BannerWriteResult(s.Aumid, false, null, "not in the list");
            if (FailFor.Contains(s.Aumid)) return new BannerWriteResult(s.Aumid, false, a.Banner, "access denied");
            if (!StickFor.Contains(s.Aumid)) a.Banner = s.Value;
            bool ok = a.Banner == s.Value;
            return new BannerWriteResult(s.Aumid, ok, a.Banner, ok ? null : "kept a different value");
        }).ToArray();
        return Task.FromResult<BannerWrite?>(new BannerWrite(results.All(x => x.Ok), null, results));
    }
}

/// The record file in memory, written through the real JSON shape (the test's fakeStore).
public sealed class FakeStore : IBannerRecordStore
{
    public string? File;
    public int Saves;
    public bool FailSave, FailLoad, FailBack;
    public readonly List<BackEntry> Back = new();
    public List<string> BackUnreadable = new();

    public JsonNode? Load(out bool exists)
    {
        if (FailLoad) throw new InvalidOperationException("unexpected token in JSON");
        exists = File != null;
        return File == null ? null : JsonNode.Parse(File);
    }
    public void Save(BannerRecord r) { if (FailSave) throw new InvalidOperationException("EPERM: the disk said no"); File = r.ToJson().ToJsonString(); Saves++; }
    public BackFiles LoadBack() { if (FailBack) throw new InvalidOperationException("EBUSY: the marker file is locked"); return new BackFiles(Back.ToList(), BackUnreadable); }

    public JsonObject? Rec => File == null ? null : (JsonObject)JsonNode.Parse(File)!;
    public BannerRecord Record => BannerRecord.FromJson(File == null ? null : JsonNode.Parse(File));
    public double? TakenOf(string k) => Record.Taken.TryGetValue(k, out var v) ? v : null;
}

public static class ToastFixtures
{
    public const string D = "com.squirrel.Discord.Discord";
    public const string H = "com.owensilva.helm";

    public static FakeReg Apps() => new FakeReg()
        .Add(H, null)                                            // never touched: Windows' default (on)
        .Add(D, 1)                                               // banner explicitly on
        .Add("electron.app.Munder Difflin", 0)                   // the user already turned its banner off
        .Add("OperaSoftware.OperaGXWebBrowser.1751952444", null, enabled: 0)   // notifications off
        .Add("Windows.SystemToast.AutoPlay", null)
        .Add("NotifyIconGeneratedAumid_1234", null)
        .Add("Microsoft.YourPhone_8wekyb3d8bbwe!App", null)
        .Add("Weird.App", null, odd: true);                      // a string ShowBanner: never Mantle's

    /// An out-of-process put-back (the sidecar's exit, the crash supervisor, restore-banners.ps1).
    public static void PutBack(FakeReg reg, FakeStore store, string from = "sidecar")
    {
        var rec = store.Record;
        foreach (var k in rec.Owned.Keys)
        {
            if (!reg.Apps.TryGetValue(k, out var a) || a.Banner != 0) continue;
            a.Banner = rec.Owned[k];
            store.Back.Add(new BackEntry(k, rec.Taken.TryGetValue(k, out var t) ? Stamp.Of(t) : Stamp.None, 0, from));
        }
    }
}
