using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.Toasts;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.Notifications;

// The notification list as the toasts feature hears it: the part of lib/notifications.js the banners ride on (its
// sidecar list + watch, the payload it publishes). The bell's own half (seen, remove, clear, the bar's chip) is the
// bar lane's when it is ported; until then this is the only reader, and it only READS: `notifications {op:"list"}`
// and `{op:"watch", on:true}` (both allowed on a passive --test sidecar: they act on nothing).
//
// On every sidecar ready: the watch is armed and the list read; on every "notifications" event (the watch saw the
// database change) the list is read again. A failed read is published too (ok:false with the reason), never dropped.

/// <summary>One read of the list: the toasts, and what it says about access (lib/notifications.js publish payload).</summary>
public sealed record NotificationList(bool Ok, bool Available, string? Access, string? Reason, IReadOnlyList<IncomingToast> Toasts)
{
    public static NotificationList Failed(string reason) => new(false, false, null, reason, Array.Empty<IncomingToast>());

    public static NotificationList Parse(PlatformReply r)
    {
        if (!r.Ok) return Failed(r.Error ?? "the notification list did not answer");
        var x = r.Result;
        if (x.ValueKind != JsonValueKind.Object) return Failed("the notification list answered with nothing");
        bool ok = !(x.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False);
        bool available = !(x.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.False);
        string? access = x.TryGetProperty("access", out var ac) && ac.ValueKind == JsonValueKind.String ? ac.GetString() : null;
        string? reason = x.TryGetProperty("reason", out var re) && re.ValueKind == JsonValueKind.String ? re.GetString() : null;
        var list = new List<IncomingToast>();
        if (x.TryGetProperty("toasts", out var ts) && ts.ValueKind == JsonValueKind.Array)
            foreach (var t in ts.EnumerateArray()) if (IncomingToast.FromJson(t) is { } it) list.Add(it);
        return new NotificationList(ok, available, access, reason, list);
    }
}

/// <summary>Reads the list through the sidecar and tells the listener every read (see the file comment).</summary>
public sealed class NotificationFeed : IDisposable
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(4);

    readonly IPlatform _platform;
    readonly Action<NotificationList> _heard;
    readonly Action<string> _log;
    readonly List<IDisposable> _offs = new();
    bool _reading, _again, _gone;

    public NotificationFeed(IPlatform platform, Action<NotificationList> heard, Action<string>? log = null)
    {
        _platform = platform; _heard = heard; _log = log ?? (_ => { });
    }

    public NotificationList? Last { get; private set; }
    public bool Watching { get; private set; }
    public string? WatchWhy { get; private set; }

    public void Start()
    {
        _offs.Add(_platform.On("notifications", ev => _ = ReadAsync("the watch saw a change")));
        _offs.Add(_platform.OnReady(() => _ = ArmAsync()));
    }

    async Task ArmAsync()
    {
        var r = await _platform.CallAsync("notifications", new JsonObject { ["op"] = "watch", ["on"] = true }, CallTimeout);
        if (_gone) return;
        bool ok = r.Ok && !(r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.False);
        Watching = ok;
        WatchWhy = ok ? null : r.Error ?? (r.Result.ValueKind == JsonValueKind.Object && r.Result.TryGetProperty("reason", out var re) ? re.GetString() : null) ?? "the watch would not start";
        if (!ok) _log($"the notification watch did not start ({WatchWhy}); new notifications are seen only when the list is read again");
        await ReadAsync("the helper is ready");
    }

    /// One read at a time; a change during a read is one more read after it.
    public async Task ReadAsync(string why)
    {
        if (_gone) return;
        if (_reading) { _again = true; return; }
        _reading = true;
        try
        {
            do
            {
                _again = false;
                var r = await _platform.CallAsync("notifications", new JsonObject { ["op"] = "list" }, CallTimeout);
                if (_gone) return;
                var l = NotificationList.Parse(r);
                if (!l.Ok && (Last == null || Last.Ok)) _log($"the notification list could not be read ({l.Reason ?? "no reason"}; {why})");
                Last = l;
                try { _heard(l); } catch (Exception ex) { _log($"a notification list listener failed ({ex.Message})"); }
            } while (_again && !_gone);
        }
        finally { _reading = false; }
    }

    public void Dispose()
    {
        _gone = true;
        foreach (var o in _offs) o.Dispose();
        _offs.Clear();
    }
}
