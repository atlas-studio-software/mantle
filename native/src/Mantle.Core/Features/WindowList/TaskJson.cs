using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Platform;

namespace Mantle.Core.Features.WindowList;

/// <summary>
/// One platform call the way lib/sidecar.js#call answers it: a result, or an exception with the plain words
/// (a refusal, a timeout, an <c>{"ok":false}</c> reply). The ported JS code is written around try/catch, so the
/// port keeps that shape; <see cref="PlatformReply"/> itself never throws.
/// </summary>
public delegate ValueTask<JsonElement> SidecarCall(string verb, JsonObject args, TimeSpan timeout);

/// Small readers for the sidecar's JSON (no reflection: Native AOT).
internal static class TaskJson
{
    public static SidecarCall From(IPlatform p) => async (verb, args, timeout) => {
        var r = await p.CallAsync(verb, args, timeout).ConfigureAwait(true);
        if (!r.Ok) throw new PlatformCallException(r.Error ?? "the platform did not say why");
        return r.Result;
    };

    public static string? Str(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static bool Bool(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static bool IsFalse(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.False;

    public static double Num(JsonElement o, string name)
    {
        if (o.ValueKind != JsonValueKind.Object || !o.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d)) return d;
        if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return 0;
    }

    public static bool Has(JsonElement o, string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out _);

    public static JsonElement Get(JsonElement o, string name)
        => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) ? v : default;

    public static string? Low(string? s) => string.IsNullOrEmpty(s) ? null : s.ToLowerInvariant();
}

/// A platform call that did not give a result (the words are the reason, as Electron's Error.message).
public sealed class PlatformCallException : Exception
{
    public PlatformCallException(string message) : base(message) { }
}
