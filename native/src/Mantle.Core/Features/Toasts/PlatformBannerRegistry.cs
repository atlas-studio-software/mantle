using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Toasts;

/// <summary>
/// The registry side of the owner through the sidecar's <c>toasts.banners</c> (sidecar/ToastBanners.cs,
/// unchanged): <c>{op:'read', record}</c> and <c>{op:'write', record, set:[{aumid, value}]}</c>. Naming the
/// record tells the sidecar where its exit put-back (ToastBanners.RestoreForExit) reads the originals from.
/// A passive (--test) sidecar refuses every write that is not a Mantle.ToastTest* id. A refusal throws, as
/// toasts.js's sc() does, so the owner reports it in words.
/// </summary>
public sealed class PlatformBannerRegistry : IBannerRegistry
{
    readonly IPlatform _p;
    readonly string _record;
    readonly TimeSpan _timeout;

    public PlatformBannerRegistry(IPlatform platform, string recordFile, TimeSpan? timeout = null)
    {
        _p = platform ?? throw new ArgumentNullException(nameof(platform));
        _record = recordFile;
        _timeout = timeout ?? TimeSpan.FromMilliseconds(4000);
    }

    async Task<JsonElement> Call(JsonObject args)
    {
        var r = await _p.CallAsync("toasts.banners", args, _timeout);
        if (!r.Ok) throw new InvalidOperationException(r.Error ?? "toasts.banners did not answer");
        return r.Result;
    }

    public async Task<BannerRead?> ReadAsync()
    {
        var x = await Call(new JsonObject { ["op"] = "read", ["record"] = _record });
        return ParseRead(x);
    }

    public async Task<BannerWrite?> WriteAsync(IReadOnlyList<BannerSet> set)
    {
        var arr = new JsonArray(set.Select(s => (JsonNode?)new JsonObject { ["aumid"] = s.Aumid, ["value"] = s.Value is int v ? JsonValue.Create(v) : null }).ToArray());
        var x = await Call(new JsonObject { ["op"] = "write", ["record"] = _record, ["set"] = arr });
        return ParseWrite(x);
    }

    static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : null;
    static bool True(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;

    public static BannerRead? ParseRead(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object) return null;
        bool ok = !(x.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.False);
        if (!x.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) return new BannerRead(false, Str(x, "reason"), null!);
        var list = new List<BannerApp>();
        foreach (var a in apps.EnumerateArray())
        {
            if (a.ValueKind != JsonValueKind.Object) continue;
            list.Add(new BannerApp(Str(a, "aumid") ?? "", Int(a, "banner"), Int(a, "enabled"), True(a, "odd")));
        }
        return new BannerRead(ok, Str(x, "reason"), list);
    }

    public static BannerWrite? ParseWrite(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object) return null;
        if (!x.TryGetProperty("results", out var res) || res.ValueKind != JsonValueKind.Array) return new BannerWrite(false, Str(x, "reason"), null!);
        var list = new List<BannerWriteResult>();
        foreach (var e in res.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Object) list.Add(new BannerWriteResult(Str(e, "aumid") ?? "", True(e, "ok"), Int(e, "now"), Str(e, "reason")));
        return new BannerWrite(True(x, "ok"), Str(x, "reason"), list);
    }
}
