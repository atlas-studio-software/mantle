using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Mantle.Core.Features.LauncherIndex;
using Mantle.Core.Features.Search;
using Xunit;

namespace Mantle.Core.Tests.Search;

/// <summary>
/// Ranking and index parity with Electron (LAU-115..183): LauncherGolden.g.cs is Electron's own
/// lib/launcher-index.js + ui/launcher/groups.js run on a fixture by native/tools/parity/launcher/make-golden.cjs.
/// The native index built from the SAME fixture must give the same rows, and the native search must give the
/// same rows in the same order with the same scores, query for query.
/// </summary>
public sealed class LauncherGoldenTests
{
    static readonly JsonElement G = JsonDocument.Parse(LauncherGolden.Json).RootElement;

    static string? S(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool B(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// The golden's items as native items (ids exactly as Electron made them).
    internal static List<LauncherItem> GoldenItems() => G.GetProperty("items").EnumerateArray().Select(FromJson).ToList();

    static LauncherItem FromJson(JsonElement o)
    {
        string? kw = null; List<string>? words = null;
        if (o.TryGetProperty("keywords", out var k))
        {
            if (k.ValueKind == JsonValueKind.String) kw = k.GetString();
            else if (k.ValueKind == JsonValueKind.Array) words = k.EnumerateArray().Select(x => x.GetString()!).ToList();
        }
        string? icon = S(o, "icon");
        return new LauncherItem
        {
            Id = S(o, "id")!, Kind = S(o, "kind")!, Name = S(o, "name")!, Subtitle = S(o, "subtitle") ?? "", Path = S(o, "path") ?? "",
            Keywords = kw, PowerWords = words, Secondary = B(o, "secondary"), MantleApp = S(o, "mantleApp"), StandsFor = S(o, "standsFor"),
            WindowsPath = S(o, "windowsPath"), Aumid = S(o, "aumid"), Glyph = S(o, "glyph"), Confirm = S(o, "confirm"),
            IconKey = icon != null && icon.StartsWith("mantle:", StringComparison.Ordinal) ? icon : null,
        };
    }

    [Fact]
    public void StableIdsMatchElectron()
    {
        foreach (var row in G.GetProperty("stableIds").EnumerateArray())
        {
            var a = row.EnumerateArray().Select(x => x.GetString()!).ToArray();
            Assert.Equal(a[2], Js.StableId(a[0], a[1]));
        }
    }

    [Fact]
    public void UrlDetectionMatchesElectron()
    {
        foreach (var row in G.GetProperty("urls").EnumerateArray())
            Assert.True(row[1].GetBoolean() == LauncherSearch.LooksLikeUrl(row[0].GetString()!), "looksLikeUrl(\"" + row[0].GetString() + "\")");
    }

    [Fact]
    public void SleepStatesParseLikeElectron()
    {
        var want = G.GetProperty("sleep");
        var got = PowerRows.ParseSleepStates(G.GetProperty("fixture").GetProperty("powercfg").GetString()!);
        Assert.Equal(want.GetProperty("ok").GetBoolean(), got.Ok);
        Assert.Equal(want.GetProperty("hibernate").GetBoolean(), got.Hibernate);
        Assert.Equal(want.GetProperty("sleep").GetBoolean(), got.Sleep);
        Assert.Equal(want.GetProperty("sleepMode").GetString(), got.SleepMode);
        Assert.Equal(want.GetProperty("fastStartup").GetBoolean(), got.FastStartup);
        Assert.Equal(want.GetProperty("hibernateWhy").GetString(), got.HibernateWhy);
    }

    /// The fixture Start Menu, profile, powercfg and Store list, written where the index reads them.
    internal sealed class Fixture : IDisposable
    {
        readonly TestDir _dir = new("launcher");
        public string Pd { get; }
        public string Ad { get; }
        public IndexInputs Inputs { get; }

        public Fixture()
        {
            var fx = G.GetProperty("fixture");
            Pd = Path.Combine(_dir.Path, "pd", "Microsoft", "Windows", "Start Menu", "Programs");
            string home = Path.Combine(_dir.Path, "home");
            Ad = Path.Combine(home, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs");
            foreach (var f in fx.GetProperty("files").EnumerateArray())
            {
                string full = Path.Combine(S(f, "root") == "pd" ? Pd : Ad, S(f, "rel")!);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, Convert.FromBase64String(S(f, "b64")!));
            }
            string profile = Path.Combine(home, "Documents", "PowerShell", "Microsoft.PowerShell_profile.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.WriteAllText(profile, S(fx, "profile")!, new UTF8Encoding(false));
            string powercfg = S(fx, "powercfg")!, store = S(fx, "store")!;
            Inputs = new IndexInputs
            {
                Roots = [new StartMenuRoot(Pd, "all users"), new StartMenuRoot(Ad, "you")],
                ProfileFiles = ProfileCommands.Candidates(home, null),
                ReadSleep = _ => Task.FromResult(PowerRows.ParseSleepStates(powercfg)),
                ReadStore = _ => Task.FromResult(new StoreRead(true, StoreApps.Parse(store), "")),
            };
        }

        public string Rel(string? p) => p == null ? "" : p.Replace(Pd, "<pd>").Replace(Ad, "<ad>");
        public void Dispose() => _dir.Dispose();
    }

    [Fact]
    public async Task IndexBuiltFromTheFixtureMatchesElectron()
    {
        using var fx = new Fixture();
        var snap = await new LauncherIndexer(fx.Inputs).BuildAsync();
        var want = G.GetProperty("items").EnumerateArray().ToList();
        var diffs = new List<string>();
        for (int i = 0; i < Math.Max(want.Count, snap.Items.Count); i++)
        {
            if (i >= want.Count) { diffs.Add("#" + i + " extra native row " + snap.Items[i].Name); continue; }
            if (i >= snap.Items.Count) { diffs.Add("#" + i + " missing row " + S(want[i], "name")); continue; }
            var w = want[i]; var g = snap.Items[i];
            void Eq(string field, string? a, string? b) { if (a != b) diffs.Add("#" + i + " " + S(w, "name") + " " + field + ": electron=" + a + " native=" + b); }
            Eq("kind", S(w, "kind"), g.Kind);
            Eq("name", S(w, "name"), g.Name);
            Eq("subtitle", S(w, "subtitle"), g.Subtitle);
            Eq("path", S(w, "path"), fx.Rel(g.Path));
            bool pathId = (S(w, "path") ?? "").Contains("<pd>") || (S(w, "path") ?? "").Contains("<ad>") || (S(w, "windowsPath") ?? "").Contains("<");
            if (!pathId) Eq("id", S(w, "id"), g.Id);
            if (w.TryGetProperty("keywords", out var kw))
            {
                if (kw.ValueKind == JsonValueKind.String) Eq("keywords", kw.GetString(), g.Keywords);
                else Eq("keywords", string.Join("|", kw.EnumerateArray().Select(x => x.GetString())), g.PowerWords == null ? null : string.Join("|", g.PowerWords));
            }
            else if (g.Keywords != null || g.PowerWords != null) diffs.Add("#" + i + " " + g.Name + " has keywords Electron does not");
            Eq("secondary", B(w, "secondary").ToString(), g.Secondary.ToString());
            Eq("mantleApp", S(w, "mantleApp"), g.MantleApp);
            Eq("standsFor", S(w, "standsFor"), g.StandsFor);
            Eq("windowsPath", S(w, "windowsPath"), g.WindowsPath == null ? null : fx.Rel(g.WindowsPath));
            Eq("aumid", S(w, "aumid"), g.Aumid);
            Eq("glyph", S(w, "glyph"), g.Glyph);
            Eq("confirm", S(w, "confirm"), g.Confirm);
            string? icon = S(w, "icon");
            Eq("icon", icon, g.IconKey);
        }
        var srcWant = G.GetProperty("sources").EnumerateArray().Select(s => S(s, "name") + " " + s.GetProperty("count").GetInt32() + " " + S(s, "status") + " " + S(s, "reason")).ToList();
        var srcGot = snap.Sources.Select(s => s.Name + " " + s.Count + " " + s.Status + " " + fx.Rel(s.Reason)).ToList();
        for (int i = 0; i < srcWant.Count; i++) if (i >= srcGot.Count || srcWant[i] != srcGot[i]) diffs.Add("source: electron=" + srcWant[i] + " native=" + (i < srcGot.Count ? srcGot[i] : "(none)"));
        Assert.True(diffs.Count == 0, "LAUNCHER INDEX PARITY: " + diffs.Count + " difference(s) over " + want.Count + " rows:\n" + string.Join("\n", diffs.Take(25)));
    }

    [Fact]
    public void SearchMatchesElectronForEveryQuery()
    {
        var items = GoldenItems();
        var cases = G.GetProperty("cases").EnumerateArray().ToList();
        var diffs = new List<string>();
        int same = 0;
        foreach (var c in cases)
        {
            string q = S(c, "q")!;
            var hits = LauncherSearch.Search(q, items);
            var want = c.GetProperty("ids").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? items[x.GetInt32()].Id : x.GetString()!).ToList();
            var wantScores = c.GetProperty("scores").EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? (int?)x.GetInt32() : null).ToList();
            var got = hits.Select(h => h.Item.Id).ToList();
            var gotScores = hits.Select(h => h.Score).ToList();
            bool ok = want.SequenceEqual(got) && wantScores.SequenceEqual(gotScores);
            if (c.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                var row = hits.FirstOrDefault(h => h.Item.Kind == "url")?.Item;
                ok &= row != null && row.Name == S(u, "name") && row.Path == S(u, "path") && row.Id == S(u, "id");
            }
            var g = LauncherGroups.Build(q, hits);
            string groups = string.Join(",", g.Groups.Select(x => x.Key + ":" + x.Rows.Count));
            var flat = c.GetProperty("flat").EnumerateArray().Select(x => x.GetString()!).ToList();
            ok &= groups == S(c, "groups") && flat.SequenceEqual(g.Flat.Select(r => r.Item.Id));
            if (ok) { same++; continue; }
            string Names(IEnumerable<string> ids) => string.Join(", ", ids.Select(id => items.FirstOrDefault(i => i.Id == id)?.Name ?? id));
            diffs.Add("\"" + q + "\"\n  electron: " + Names(want) + " [" + string.Join(",", wantScores) + "] groups " + S(c, "groups") +
                      "\n  native:   " + Names(got) + " [" + string.Join(",", gotScores) + "] groups " + groups);
        }
        Assert.True(diffs.Count == 0, "LAUNCHER RANKING PARITY: " + same + " of " + cases.Count + " queries identical; " + diffs.Count + " differ:\n" + string.Join("\n", diffs.Take(15)));
    }

    [Fact]
    public void AppsSortLikeElectronLocaleCompare()
    {
        // the order of the golden's apps IS Electron's localeCompare over names with case, digits, punctuation and accents
        var apps = GoldenItems().Where(i => i.Kind == "app").Select(i => i.Name).ToList();
        var sorted = Js.StableSort(apps, Js.LocaleCompare);
        Assert.Equal(apps, sorted);
    }
}
