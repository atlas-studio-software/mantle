using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mantle.Core.Features.TopBar;

// TOP-013 / TOP-016: an app with no Win32 menu whose focus snapshot says uia "try" gets ONE `topbar.uia` read per window
// handle (ui/topbar/topbar.js takeFocus). The result is kept per handle (a failed read as an empty menu with its
// reason) and put on the focus snapshot as its menu, so the strip draws the app's own menus; the surface re-renders only
// when no menu is open (an open one keeps its titles and redraws on close). Electron's page kept the cache; natively the
// feature does, so every surface gets the same menu and a window is read once per run, not once per page load.
public sealed partial class TopBarFeature
{
    public const int UiaKeep = 64;   // handles kept (the oldest goes first)
    readonly Dictionary<string, JsonObject> _uia = new(StringComparer.Ordinal);
    readonly Queue<string> _uiaOrder = new();
    readonly HashSet<string> _uiaAsking = new(StringComparer.Ordinal);

    /// Read by the tests: how many window handles have a kept UI Automation menu.
    public int UiaKept => _uia.Count;

    /// A new focus snapshot: put its kept UIA menu on it, or ask for one once.
    void UiaFor(JsonObject f)
    {
        if (f["menu"] is JsonObject || S(f, "uia") != "try" || S(f, "hwnd") is not { Length: > 0 } h) return;
        if (_uia.TryGetValue(h, out var kept)) { f["menu"] = kept.DeepClone(); return; }
        if (!_uiaAsking.Add(h)) return;
        _ = Run(async () => { await UiaRead(h); return null; }, "the app's UI Automation menu");
    }

    async Task UiaRead(string h)
    {
        JsonObject menu;
        try
        {
            var r = await _ctx.Platform.CallAsync("topbar.uia", new JsonObject { ["hwnd"] = h }, TimeSpan.FromSeconds(15));
            JsonObject? got = null; string? why = r.Ok ? null : r.Error;
            if (r.Ok && r.Result.ValueKind == JsonValueKind.Object)
            {
                if (r.Result.TryGetProperty("menu", out var m) && m.ValueKind == JsonValueKind.Object) got = JsonNode.Parse(m.GetRawText()) as JsonObject;
                else why = Str(r.Result, "reason") ?? Str(r.Result, "error");
            }
            menu = got ?? new JsonObject { ["source"] = "none", ["items"] = new JsonArray(), ["reason"] = why ?? "no reason came back" };
        }
        finally { _uiaAsking.Remove(h); }
        if (_stopped) return;
        if (!_uia.ContainsKey(h)) { _uiaOrder.Enqueue(h); while (_uiaOrder.Count > UiaKeep) _uia.Remove(_uiaOrder.Dequeue()); }
        _uia[h] = menu;
        if (_focus != null && S(_focus, "hwnd") == h && _focus["menu"] is not JsonObject)
        {
            _focus["menu"] = menu.DeepClone();
            Broadcast("topbar.focus", FocusPayload());
        }
    }
}
