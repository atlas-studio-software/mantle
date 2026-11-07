using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Xunit;

namespace Mantle.Core.Tests.Features;

// main.js COMMANDS['features.list'] / ['features.fire'] (lib/features.js list, fireChord, fireKey), the feature
// seam's own failure, and the page pieces (main.js pageReviver report).
public class FeatureCommandsTests
{
    [Fact]
    public void FeaturesList_AnswersEveryFeatureInTheShapeSettingsReads() => HostLoop.Run(async (loop, t) =>
    {
        var rig = new Rig(t);
        var opts = new[]
        {
            new FeatureOption("size", "Size", FeatureOptionKind.Choice, "m") { Choices = new[] { new FeatureChoice("s", "Small"), new FeatureChoice("m", "") } },
            new FeatureOption("delay", "Delay", FeatureOptionKind.Number, 5.0) { Min = 1, Max = 30, Unit = "s" },
        };
        rig.Host.Load(new[] { rig.D("osd", chords: new[] { "VolumeUp" }, options: opts, label: "Volume pop-up"), rig.D("notes", defaultOn: false) });
        await rig.Host.Installed;
        using var reg = new FeatureCommands(rig.Host).Register(rig.Bus);
        var r = await rig.Bus.CallAsync("features.list");
        Assert.True(r.Ok);
        var list = r.Result.EnumerateArray().ToArray();
        Assert.Equal(new[] { "notes", "osd" }, list.Select(x => x.GetProperty("name").GetString()).OrderBy(x => x));
        var osd = list.Single(x => x.GetProperty("name").GetString() == "osd");
        foreach (var k in new[] { "name", "label", "chords", "observe", "enabled", "ok", "reason", "running", "stopped", "resting", "commands", "loaded", "keys", "keysReason", "broken", "options" })
            Assert.True(osd.TryGetProperty(k, out _), $"features.list has no \"{k}\"");
        Assert.Equal("Volume pop-up", osd.GetProperty("label").GetString());
        Assert.Equal("VolumeUp", osd.GetProperty("chords")[0].GetString());
        Assert.True(osd.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, osd.GetProperty("resting").ValueKind);
        var size = osd.GetProperty("options")[0];
        Assert.Equal("choice", size.GetProperty("type").GetString());
        Assert.Equal("m", size.GetProperty("value").GetString());
        Assert.True(size.GetProperty("isDefault").GetBoolean());
        Assert.Equal("[{\"value\":\"s\",\"label\":\"Small\"},{\"value\":\"m\",\"label\":\"m\"}]", size.GetProperty("choices").GetRawText());
        var delay = osd.GetProperty("options")[1];
        Assert.Equal(1, delay.GetProperty("min").GetDouble());
        Assert.Equal("s", delay.GetProperty("unit").GetString());
        Assert.False(delay.TryGetProperty("choices", out _));
        Assert.False(list.Single(x => x.GetProperty("name").GetString() == "notes").GetProperty("enabled").GetBoolean());
    });

    [Fact]
    public void FeaturesFire_DrivesAChordInATestRun_AndIsNeverThereLive() => HostLoop.Run(async (loop, t) =>
    {
        var rig = new Rig(t);
        var hits = 0;
        rig.Host.Load(new[] { rig.D("snap", f => f.Install = c => { c.OnChord("Win+Z", _ => hits++); return ValueTask.CompletedTask; }, chords: new[] { "Win+Z" }) });
        await rig.Host.Installed;
        using var reg = new FeatureCommands(rig.Host).Register(rig.Bus);
        var r = await rig.Bus.CallAsync("features.fire", new JsonObject { ["chord"] = "Win+Z" }, "test");
        Assert.True(r.Ok);
        Assert.Equal(1, hits);
        Assert.True(r.Result.GetProperty("ok").GetBoolean());
        Assert.Equal("snap", r.Result.GetProperty("feature").GetString());
        var none = await rig.Bus.CallAsync("features.fire", new JsonObject(), "test");
        Assert.Equal("features.fire needs {chord} or {key}", none.Error);

        var live = new Rig(t, test: false);
        live.Host.Load(Array.Empty<FeatureDescriptor>());
        using var liveReg = new FeatureCommands(live.Host).Register(live.Bus);
        Assert.True(live.Bus.Has("features.list"));
        Assert.False(live.Bus.Has("features.fire"));
        Assert.Equal("features.fire works only in a test run (a live run uses the real keys)", (await new FeatureCommands(live.Host).Fire(default)).Error);
    });

    [Fact]
    public void ASeamThatThrows_IsAPieceInWords_AndMantleKeepsRunning() => HostLoop.Run(async (loop, t) =>
    {
        var rig = new Rig(t);
        Assert.Null(FeatureSeam.Start(rig.Hub, () => { }));
        Assert.Equal("no feature started: the feature seam threw the catalog is empty",
            FeatureSeam.Start(rig.Hub, () => throw new InvalidOperationException("the catalog is empty"), rig.Lines.Add));
        var p = rig.Hub.Piece("seam");
        Assert.Equal("Features", p.Label);
        Assert.True(p.On);
        Assert.False(p.Ok);
        Assert.True(rig.Logged("the feature seam could not start: System.InvalidOperationException: the catalog is empty"));
        await Task.CompletedTask;
    });

    [Fact]
    public void ADeadMainPage_IsAFeatureShapedPiece_SoTheStripAndTrayShowIt() => HostLoop.Run(async (loop, t) =>
    {
        var rig = new Rig(t);
        Assert.True(FeatureSeam.ReportPage(rig.Hub, "bar", false, "the bar page stopped answering; reloading it (1 of 3)"));
        var p = rig.Hub.Piece("page.bar");
        Assert.Equal("The bar page", p.Label);
        Assert.False(p.Ok);
        Assert.False(FeatureSeam.ReportPage(rig.Hub, "bar", false, "the bar page stopped answering; reloading it (1 of 3)"));   // nothing moved
        FeatureSeam.ReportPage(rig.Hub, "desktop", true, "running again");
        Assert.Equal("The desktop page", rig.Hub.Piece("page.desktop").Label);
        Assert.True(rig.Hub.Piece("page.desktop").Ok);
        Assert.Throws<ArgumentException>(() => FeatureSeam.ReportPage(rig.Hub, "osd", false, "x"));
        await Task.CompletedTask;
    });
}
