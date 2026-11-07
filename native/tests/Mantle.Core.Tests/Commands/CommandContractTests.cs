using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Xunit;

namespace Mantle.Core.Tests.Commands;

/// The command bus and event contracts compile and can be met: a minimal bus and event hub (test
/// doubles only; native-core writes the real ones) behave as Electron's COMMANDS and broadcast/replay do.
public class CommandContractTests
{
    sealed class Sample
    {
        public int Pins;

        [Command("bar.pin")]
        public ValueTask<CommandResult> Pin(CommandCall call)
        {
            Pins++;
            var app = call.Args.TryGetProperty("app", out var a) ? a.GetString() : null;
            return ValueTask.FromResult(app == null
                ? CommandResult.Fail("bar.pin needs an app")
                : CommandResult.Success(JsonDocument.Parse("{\"pinned\":true}").RootElement.Clone()));
        }
    }

    [Fact]
    public async Task A_Command_method_registers_under_its_Electron_name_and_answers_through_the_bus()
    {
        var sample = new Sample();
        var method = typeof(Sample).GetMethod(nameof(Sample.Pin))!;
        var attr = method.GetCustomAttribute<CommandAttribute>()!;
        Assert.Equal("bar.pin", attr.Name);

        ICommandBus bus = new TestBus();
        using (bus.Register(new CommandRegistration(attr.Name, "bar", sample.Pin)))
        {
            Assert.True(bus.Has("bar.pin"));
            var ok = await bus.CallAsync("bar.pin", new JsonObject { ["app"] = "notepad" }, "test");
            Assert.True(ok.Ok, ok.Error);
            Assert.True(ok.Result.GetProperty("pinned").GetBoolean());
            var bad = await bus.CallAsync("bar.pin");
            Assert.False(bad.Ok);
            Assert.Equal("bar.pin needs an app", bad.Error);
            var dup = Assert.Throws<InvalidOperationException>(() => bus.Register(new CommandRegistration("bar.pin", "launcher", sample.Pin)));
            Assert.Contains("bar", dup.Message);
        }
        Assert.False(bus.Has("bar.pin"));
        var gone = await bus.CallAsync("bar.pin", new JsonObject { ["app"] = "notepad" });
        Assert.Equal("unknown command: bar.pin", gone.Error);
        Assert.Equal(2, sample.Pins);
        Assert.Throws<ArgumentException>(() => CommandResult.Fail(""));
    }

    [Fact]
    public void Events_are_typed_and_a_replay_source_gives_new_subscribers_the_current_value()
    {
        IEvents events = new TestEvents();
        int volume = 40;
        var seen = new List<int>();
        using var src = events.Replay("osd.volume", () => volume);
        using var sub = events.Subscribe<int>("osd.volume", seen.Add);
        volume = 55;
        events.Publish("osd.volume", volume);
        Assert.Equal(new[] { 40, 55 }, seen);
        Assert.Throws<InvalidOperationException>(() => events.Replay("osd.volume", () => 0));
    }

    /// The smallest ICommandBus that keeps the contract's words.
    sealed class TestBus : ICommandBus
    {
        readonly Dictionary<string, CommandRegistration> _all = new(StringComparer.Ordinal);

        public IReadOnlyCollection<string> Names => _all.Keys;

        public bool Has(string name) => _all.ContainsKey(name);

        public IDisposable Register(CommandRegistration command)
        {
            if (_all.TryGetValue(command.Name, out var taken))
                throw new InvalidOperationException($"\"{command.Name}\" is {taken.Owner}'s already; {command.Owner} cannot register it too");
            _all.Add(command.Name, command);
            return new Undo(() => _all.Remove(command.Name));
        }

        public async ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null, string caller = "host", CancellationToken cancel = default)
        {
            if (!_all.TryGetValue(name, out var c)) return CommandResult.Fail($"unknown command: {name}");
            using var doc = JsonDocument.Parse(args?.ToJsonString() ?? "{}");
            try { return await c.Handler(new CommandCall(name, doc.RootElement.Clone(), caller, cancel)); }
            catch (Exception e) { return CommandResult.Fail($"command failed: {e.Message}"); }
        }
    }

    sealed class TestEvents : IEvents
    {
        readonly Dictionary<string, List<Delegate>> _subs = new(StringComparer.Ordinal);
        readonly Dictionary<string, Delegate> _replay = new(StringComparer.Ordinal);

        public void Publish<T>(string name, T payload)
        {
            if (_subs.TryGetValue(name, out var list))
                foreach (var d in list.ToArray()) ((Action<T>)d)(payload);
        }

        public IDisposable Subscribe<T>(string name, Action<T> handler)
        {
            if (!_subs.TryGetValue(name, out var list)) _subs[name] = list = new List<Delegate>();
            list.Add(handler);
            if (_replay.TryGetValue(name, out var cur)) handler(((Func<T>)cur)());
            return new Undo(() => list.Remove(handler));
        }

        public IDisposable Replay<T>(string name, Func<T> current)
        {
            if (_replay.ContainsKey(name)) throw new InvalidOperationException($"\"{name}\" already has a replay source");
            _replay[name] = current;
            return new Undo(() => _replay.Remove(name));
        }
    }

    sealed class Undo : IDisposable
    {
        Action? _undo;
        public Undo(Action undo) { _undo = undo; }
        public void Dispose() { Interlocked.Exchange(ref _undo, null)?.Invoke(); }
    }
}
