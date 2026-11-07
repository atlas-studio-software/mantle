using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Features;
using Mantle.Core.Platform;
using Mantle.Core.Tests.Platform;
using Xunit;

namespace Mantle.Core.Tests.Features;

/// The feature contract compiles and a feature can be written against it: a sample [Feature] (the
/// shape of lib/features/README.md's clipboard example) installs through a test context, registers a
/// command and a chord, reports health, asks the platform, and stops.
public class FeatureContractTests
{
    [Feature("clipboard", "Clipboard history (Win+V)", defaultOn: true, Chords = new[] { "Win+V" }, Rest = true)]
    sealed class SampleFeature : IFeature
    {
        public int Shown;
        public FeatureStop? StoppedWith;

        public async ValueTask InstallAsync(IFeatureContext ctx)
        {
            var r = await ctx.Platform.CallAsync("clip.start");
            ctx.Health(r.Ok, r.Ok ? "the clipboard listener is running" : r.Error);
            ctx.Command("list", async call =>
            {
                var list = await ctx.Platform.CallAsync("clip.list");
                return list.Ok ? CommandResult.Success(list.Result) : CommandResult.Fail(list.Error!);
            });
            ctx.OnChord("Win+V", _ => { if (ctx.Enabled) Shown++; });
        }

        public ValueTask StopAsync(FeatureStop stop) { StoppedWith = stop; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task A_feature_installs_against_the_context_and_stops_with_its_reason()
    {
        var attr = typeof(SampleFeature).GetCustomAttribute<FeatureAttribute>()!;
        var d = FeatureDescriptor.Of(attr, () => new SampleFeature(),
            new[] { new FeatureOption("max", "Keep", FeatureOptionKind.Number, 25.0) { Min = 5, Max = 200, Unit = "items" } });
        Assert.Equal("clipboard", d.Name);
        Assert.True(d.DefaultOn);
        Assert.True(d.Rest);
        Assert.Equal(new[] { "Win+V" }, d.Chords);
        Assert.Empty(d.Observe);
        Assert.Equal(FeatureOptionKind.Number, Assert.Single(d.Options).Kind);

        var platform = new FakePlatform().Answer("clip.start", "{}").Answer("clip.list", "{\"items\":[]}");
        platform.Start();
        var ctx = new TestContext("clipboard", platform);
        var f = (SampleFeature)d.Create();
        await f.InstallAsync(ctx);

        Assert.True(ctx.Ok);
        Assert.Equal("the clipboard listener is running", ctx.Reason);
        var list = await ctx.Commands["clipboard.list"](new CommandCall("clipboard.list", default, "test", CancellationToken.None));
        Assert.True(list.Ok, list.Error);
        Assert.Equal(JsonValueKind.Array, list.Result.GetProperty("items").ValueKind);

        ctx.Chords["Win+V"]("Win+V");
        Assert.Equal(1, f.Shown);
        Assert.Throws<InvalidOperationException>(() => ctx.OnChord("Win+S", _ => { }));   // not in its chord list

        await f.StopAsync(new FeatureStop("VALORANT runs", Rest: true));
        Assert.Equal("VALORANT runs", f.StoppedWith!.Value.Reason);
        Assert.True(f.StoppedWith!.Value.Rest);
    }

    /// The smallest IFeatureContext a feature can run against (native-feature-host writes the real one).
    sealed class TestContext : IFeatureContext
    {
        readonly string[] _chords = { "Win+V" };
        readonly CancellationTokenSource _stopping = new();

        public TestContext(string name, IPlatform platform) { Name = name; Platform = platform; }

        public Dictionary<string, CommandHandler> Commands { get; } = new();
        public Dictionary<string, Action<string>> Chords { get; } = new();
        public bool Ok { get; private set; }
        public string? Reason { get; private set; }
        public List<string> Lines { get; } = new();

        public string Name { get; }
        public FeatureFlags Flags => new(Test: true, Live: false);
        public IPlatform Platform { get; }
        public bool Enabled => true;
        public IStore Settings => throw new NotSupportedException("the store contract has its own test (Data/StoreContractTests)");
        public CancellationToken Stopping => _stopping.Token;

        public void Log(string text) => Lines.Add(Name + ": " + text.Replace('\n', ' '));

        public IDisposable Command(string verb, CommandHandler handler)
        {
            string full = Name + "." + verb;
            Commands.Add(full, handler);
            return new Undo(() => Commands.Remove(full));
        }

        public IDisposable OnChord(string chord, Action<string> handler)
        {
            if (Array.IndexOf(_chords, chord) < 0) throw new InvalidOperationException($"{Name} does not list the chord {chord}");
            Chords[chord] = handler;
            return new Undo(() => Chords.Remove(chord));
        }

        public IDisposable OnKey(string key, Action<string> handler) => throw new InvalidOperationException($"{Name} observes no keys");

        public void Health(bool ok, string? reason = null) { Ok = ok; Reason = reason; }

        public IDisposable OnEnabled(Action<bool> handler) => new Undo(() => { });

        public IDisposable OnOption(string key, Action<JsonElement, string> handler) => new Undo(() => { });

        public void Broadcast<T>(string ev, T payload) { }

        public IDisposable Replay<T>(string ev, Func<T> current) => new Undo(() => { });
    }

    sealed class Undo : IDisposable
    {
        Action? _undo;
        public Undo(Action undo) { _undo = undo; }
        public void Dispose() { var u = _undo; _undo = null; u?.Invoke(); }
    }
}
