using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features.QuickSettings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Mantle.App.Surfaces.Bar.QuickSettings;

/// <summary>
/// Quick settings' second tile row (ui/bar/qs-tiles.js): Airplane mode, Do not disturb, Focus, Project, Cast, in the
/// same two-column grid after the four first tiles, with their own notes strip under the grid (.qsx-notes). The row
/// paints from the state the bar already holds (<c>qs.tiles.state</c>, pushed as <c>"qs.tiles"</c>) and asks
/// <c>qs.tiles.refresh</c> only after the panel's first frame. A click moves the tile at once (pending, from
/// <see cref="QsTiles.Guess"/>) and the reply decides; a refusal, or a --test run's "Test run: would ..." words, is the
/// tile's note until it is clicked again. Project and Cast open Windows' own choices (closing the panel); in a --test
/// run nothing opens and the note says what would have. Ids: qs.tile.&lt;key&gt;, qs.tiles.notes, qs.tiles.note.&lt;n&gt;.
/// </summary>
internal sealed partial class QsPanel
{
    readonly StackPanel _xNotes = new() { Margin = new Thickness(0, -4, 0, 10), Visibility = Visibility.Collapsed };
    readonly Dictionary<string, string> _xNote = new(StringComparer.Ordinal);
    readonly HashSet<string> _xBusy = new(StringComparer.Ordinal);
    JsonObject? _xState, _xGuess;
    int _xGen;
    Grid _grid = null!;

    /// Dark tiles are Chromium's 44.7 px (13 x 1.1 + 12 x 1.2 + 16) at a 50.7 px pitch, and Chromium snaps each box's
    /// edges to whole pixels where they land, so the five rows come out 44, 45, 45, 44, 45 (the pair's dark crop,
    /// run 42812366552); a TextBlock's whole-pixel lines make every native row 44, four short over the grid. Light
    /// tiles land within a pixel of Chromium's with Auto rows.
    static readonly double[] DarkRows = { 44, 45, 45, 44, 45 };

    void SizeRows()
    {
        for (int r = 0; r < _grid.RowDefinitions.Count; r++)
            _grid.RowDefinitions[r].Height = _dark && r < DarkRows.Length ? new GridLength(DarkRows[r]) : GridLength.Auto;
    }

    static double WallMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    void BuildTiles(Grid grid, int i)
    {
        _grid = grid;
        foreach (var (key, label) in QsTiles.Order) {
            var t = MakeTile(key, label);
            int row = i / 2;
            while (grid.RowDefinitions.Count <= row) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetColumn(t.B, i % 2); Grid.SetRow(t.B, row);
            grid.Children.Add(t.B);
            _tiles[key] = t; i++;
            string k = key;
            t.B.Click += (_, _) => _ = TileClick(k);
        }
        AutomationProperties.SetAutomationId(_xNotes, "qs.tiles.notes");
        AutomationProperties.SetName(_xNotes, "Tile notes");
    }

    JsonObject? XState => _xGuess ?? _xState;
    QsTileView XView(string k) => QsTiles.View(k, XState, WallMs());

    async Task TilesRefresh()
    {
        if (_link == null) return;
        var r = await _link.CallAsync("qs.tiles.refresh");
        if (r.Ok && Obj(r.Result) is JsonObject s && s.ContainsKey("dnd")) _xState = s;
        if (Open) Paint();
    }

    /// After the panel's first frame: fresh reads (never before it), and Focus's minutes every 20 s while it is up.
    void TilesOpened()
    {
        int gen = ++_xGen;
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (gen == _xGen && Open) _ = TilesRefresh(); });
        _ = FocusTicks(gen);
    }

    async Task FocusTicks(int gen)
    {
        while (true) {
            await Task.Delay(20000);
            if (gen != _xGen || !Open) return;
            if (XState?["focus"] is JsonObject f && QsParts.IsTrue(f["on"])) Paint();
        }
    }

    void TilesClosed() => _xGen++;

    /// qs-tiles.js click().
    async Task TileClick(string k)
    {
        var v = XView(k);
        _xNote.Remove(k);
        string name = QsTiles.Name(k);
        if (k is "project" or "cast") {
            if (v.Dead) { _xNote[k] = v.Tip.Split('\n')[0]; Paint(); return; }
            if (_link == null) { _xNote[k] = name + " did not open: quick settings is still starting"; Paint(); return; }
            var o = await _link.CallAsync("qs.tiles.open", new JsonObject { ["tile"] = k });
            var res = o.Ok ? Obj(o.Result) : null;
            if (res == null) _xNote[k] = name + " did not open: " + QsService.Plain(o.Error);
            else if (!QsParts.IsOk(res)) _xNote[k] = name + ": " + (QsParts.Str(res["reason"]) ?? "it did not open");
            else if (QsParts.IsTrue(res["dryRun"])) _xNote[k] = QsTiles.Note(k, true, QsParts.Str(res["reason"])) ?? "";
            else { _room.Close?.Invoke($"{name} opened Windows' own panel"); return; }
            Paint();
            return;
        }
        if (v.Dead) { _xNote[k] = v.Tip.Split('\n')[0]; Paint(); return; }
        if (v.Reading) { _xNote[k] = name + " is still being read, so it cannot be switched yet"; Paint(); return; }
        if (_xBusy.Contains(k)) return;
        bool want = !v.On;
        _xBusy.Add(k);
        _xGuess = QsTiles.Guess(_xState, k, want, WallMs());   // the tile moves in this frame; the reply decides
        Paint();
        if (_link == null) { _xBusy.Remove(k); _xGuess = null; _xNote[k] = name + " did not change: quick settings is still starting"; Paint(); return; }
        var r = await _link.CallAsync("qs.tiles.set", new JsonObject { ["tile"] = k, ["on"] = want });
        _xBusy.Remove(k); _xGuess = null;
        var got = r.Ok ? Obj(r.Result) : null;
        if (got?["state"] is JsonObject st) _xState = (JsonObject)st.DeepClone();
        if (got == null) _xNote[k] = name + " did not change: " + QsService.Plain(r.Error);
        else if (QsTiles.Note(k, QsParts.IsOk(got), QsParts.Str(got["reason"])) is string n) _xNote[k] = n;
        Paint();
    }

    void PaintTiles()
    {
        SizeRows();
        var busy = new HashSet<string>(_xBusy, StringComparer.Ordinal);
        if (XState?["busy"] is JsonArray ba) foreach (var b in ba) if (QsParts.Str(b) is string s) busy.Add(s);
        foreach (var (k, _) in QsTiles.Order) PaintTile(XView(k), TileGlyph(k), busy.Contains(k));
        _xNotes.Children.Clear();
        int i = 0;
        foreach (var line in _xNote.Values) {
            var t = Text(line, Fs);
            t.TextWrapping = TextWrapping.Wrap; Type(t, Fs, 1.45); t.Foreground = B("LgWarn");
            t.Padding = new Thickness(0, i == 0 ? 6 : 2, 0, 0);   // .qsnotes .note: 6 above, 2 between
            AutomationProperties.SetAutomationId(t, "qs.tiles.note." + i);
            AutomationProperties.SetName(t, line);
            _xNotes.Children.Add(t);
            i++;
        }
        bool show = _xNotes.Children.Count > 0;
        if (show != (_xNotes.Visibility == Visibility.Visible)) {
            _xNotes.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (Open && _view == null) Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => Fit());   // a note is not cut off
        }
    }

    // qs-tiles.js GLYPH, 16x16 at stroke 1.3 (circles and rounded rects as arcs: Fill draws paths only)
    static string[] TileGlyph(string k) => k switch {
        "airplane" => AirplanePaths,
        "dnd" => DndPaths,
        "focus" => FocusPaths,
        "project" => ProjectPaths,
        _ => CastPaths,
    };
    static readonly string[] AirplanePaths = { "S|M8 1.8c.6 0 1 .5 1 1.2v3.6l4.6 2.6v1.4L9 9.3v2.6l1.6 1.2v1.1L8 13.5l-2.6.7v-1.1L7 11.9V9.3l-4.6 1.3V9.2L7 6.6V3c0-.7.4-1.2 1-1.2z" };
    static readonly string[] DndPaths = { "S|M4.3 11.2V7.4a3.7 3.7 0 0 1 6.2-2.7M11.7 7.4v3.8l1 1.1H5.2M6.6 13.6a1.5 1.5 0 0 0 2.8 0M2.6 2.6l10.8 10.8" };
    static readonly string[] FocusPaths = {
        "S|M2.1 8a5.9 5.9 0 1 0 11.8 0a5.9 5.9 0 1 0 -11.8 0z", "S|M5 8a3 3 0 1 0 6 0a3 3 0 1 0 -6 0z", "F|M7.1 8a.9 .9 0 1 0 1.8 0a.9 .9 0 1 0 -1.8 0z" };
    static readonly string[] ProjectPaths = {
        "S|M2.7 3h5.6a1.2 1.2 0 0 1 1.2 1.2v3.6a1.2 1.2 0 0 1 -1.2 1.2h-5.6a1.2 1.2 0 0 1 -1.2 -1.2v-3.6a1.2 1.2 0 0 1 1.2 -1.2z",
        "S|M7.7 6.5h5.6a1.2 1.2 0 0 1 1.2 1.2v3.6a1.2 1.2 0 0 1 -1.2 1.2h-5.6a1.2 1.2 0 0 1 -1.2 -1.2v-3.6a1.2 1.2 0 0 1 1.2 -1.2z" };
    static readonly string[] CastPaths = {
        "S|M1.8 5.5V4.2c0-.8.6-1.4 1.4-1.4h9.6c.8 0 1.4.6 1.4 1.4v7.6c0 .8-.6 1.4-1.4 1.4H9.6M1.8 8.4a4.8 4.8 0 0 1 4.8 4.8M1.8 10.8a2.4 2.4 0 0 1 2.4 2.4",
        "F|M1.3 13.1a.7 .7 0 1 0 1.4 0a.7 .7 0 1 0 -1.4 0z" };
}
