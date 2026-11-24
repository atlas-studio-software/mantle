using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Emoji;

/// <summary>
/// What the user picked, kept the way lib/features/emoji.js keeps it (EMO-036..039): the emoji feature's own settings file
/// <c>features\emoji.json</c> (plain JSON through the feature's <see cref="IStore"/>, so tmp + rename, unknown keys kept,
/// the <c>.unreadable</c> rule and the write gate all come from the data layer) with <c>recents</c> (newest first, at most
/// 32), <c>tone</c> (0..5) and <c>apps</c> (exe file name -> its picks, at most 12 each and 30 apps, the oldest app dropped).
/// A refused or failed save keeps the change in memory for this run and answers the reason; nothing here logs an emoji.
/// </summary>
public sealed class EmojiPicks
{
    public const int PerApp = 12, AppsMax = 30;
    public const int AppRowMin = 3, AppRowMax = 16;   // ui/emoji/emoji.js: "Used in <App>" from 3 picks, at most 16 shown

    readonly IStore _store;
    List<string> _recents;
    int _tone;
    List<KeyValuePair<string, List<string>>> _apps;

    public EmojiPicks(IStore settings)
    {
        _store = settings ?? throw new ArgumentNullException(nameof(settings));
        _recents = _store.TryGet("recents", out var r) ? Arr(r) : new List<string>();
        _tone = _store.TryGet("tone", out var t) ? EmojiModel.ClampTone(t) : 0;
        _apps = _store.TryGet("apps", out var a) ? Apps(a) : new List<KeyValuePair<string, List<string>>>();
    }

    public IReadOnlyList<string> Recents => _recents;
    public int Tone => _tone;
    /// The option "Close after picking" (closeAfterInsert, default off).
    public bool CloseAfterInsert => _store.TryGet("closeAfterInsert", out var v) && v.ValueKind == JsonValueKind.True;
    /// The app names in stored order (oldest first).
    public IReadOnlyList<string> AppKeys => _apps.Select(p => p.Key).ToList();

    /// lib/features/emoji.js arr(): only strings of at most 64 UTF-16 units.
    static List<string> Arr(JsonElement v) => EmojiModel.Entries(v).Where(x => x != null && x.Length <= 64).Select(x => x!).ToList();

    static List<KeyValuePair<string, List<string>>> Apps(JsonElement v)
    {
        var o = new List<KeyValuePair<string, List<string>>>();
        if (v.ValueKind != JsonValueKind.Object) return o;
        foreach (var p in v.EnumerateObject()) o.Add(new(p.Name, Arr(p.Value)));
        return o;
    }

    /// <summary>The picks made in <paramref name="exe"/> (at most 12), for the "Used in" row.</summary>
    public IReadOnlyList<string> AppPicks(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return Array.Empty<string>();
        foreach (var p in _apps) if (p.Key == exe) return p.Value.Take(PerApp).ToList();
        return Array.Empty<string>();
    }

    /// <summary>"discord.exe" -> "Discord" (the exe file name only, capitalised, without .exe).</summary>
    public static string? AppName(string? exe)
    {
        if (string.IsNullOrEmpty(exe)) return null;
        string n = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe.Substring(0, exe.Length - 4) : exe;
        return n.Length == 0 ? n : char.ToUpperInvariant(n[0]) + n.Substring(1);
    }

    /// <summary>A pick (typed, copied or a dry run) goes to the front of the recents and of its app's list, in ONE write.</summary>
    public ValueTask<StoreWrite> RememberAsync(string ch, string? exe)
    {
        if (string.IsNullOrEmpty(ch)) throw new ArgumentException("a pick has its text", nameof(ch));
        _recents = EmojiModel.Remember(_recents, ch);
        bool app = !string.IsNullOrEmpty(exe);
        if (app)
        {
            int at = _apps.FindIndex(p => p.Key == exe);
            var mine = at >= 0 ? _apps[at].Value : new List<string>();
            if (at >= 0) _apps.RemoveAt(at);
            _apps.Add(new(exe!, EmojiModel.Remember(mine, ch).Take(PerApp).ToList()));
            while (_apps.Count > AppsMax) _apps.RemoveAt(0);
        }
        // copies: the nodes are made inside the edit, so a retried edit never re-parents a node
        var recents = _recents.ToList();
        var apps = app ? _apps.Select(p => new KeyValuePair<string, List<string>>(p.Key, p.Value.ToList())).ToList() : null;
        return _store.UpdateAsync(doc =>
        {
            doc["recents"] = Node(recents);
            if (apps != null) doc["apps"] = AppsNode(apps);
        });
    }

    /// <summary>The tone row's choice (clamped: anything but a whole 0..5 is the default).</summary>
    public ValueTask<StoreWrite> SetToneAsync(int tone)
    {
        _tone = EmojiModel.ClampTone(tone);
        return _store.SetAsync("tone", JsonValue.Create(_tone));
    }

    static JsonArray Node(IEnumerable<string> list)
    {
        var a = new JsonArray();
        foreach (var s in list) a.Add(JsonValue.Create(s));
        return a;
    }

    static JsonObject AppsNode(IEnumerable<KeyValuePair<string, List<string>>> apps)
    {
        var o = new JsonObject();
        foreach (var p in apps) o[p.Key] = Node(p.Value);
        return o;
    }
}
