using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Data;
using Xunit;

namespace Mantle.Core.Tests.Data;

/// The IStore contract compiles and can be met: an in-memory store (a test double; native-data-look
/// writes the file-backed one) keeps unknown keys, sees its own writes at once, and says why on failure.
public class StoreContractTests
{
    [Fact]
    public async Task Set_keeps_every_other_key_and_is_visible_at_once()
    {
        IStores stores = new MemoryStores();
        IStore s = stores.Open(@"features\clipboard.json");
        Assert.Same(s, stores.Open(@"features\clipboard.json"));   // one instance per file
        ((MemoryStore)s).Load("{\"max\":25,\"fromANewerBuild\":{\"x\":1}}");

        var w = await s.SetAsync("max", 50);
        Assert.True(w.Ok, w.Reason);
        Assert.True(s.TryGet("max", out var max));
        Assert.Equal(50, max.GetInt32());
        Assert.Equal(1, s.Snapshot().GetProperty("fromANewerBuild").GetProperty("x").GetInt32());

        await s.UpdateAsync(doc => { doc["a"] = true; doc["b"] = "two"; });
        await s.RemoveAsync("a");
        Assert.False(s.TryGet("a", out _));
        Assert.Equal("{\"max\":50,\"fromANewerBuild\":{\"x\":1},\"b\":\"two\"}", s.Snapshot().GetRawText());
        Assert.Equal(ReadState.Ok, s.State);
        Assert.Equal(@"features\clipboard.json", s.Name);
    }

    [Fact]
    public void A_missing_file_reads_as_an_empty_object_with_its_reason()
    {
        IStore s = new MemoryStores().Open("theme.json");
        Assert.Equal(ReadState.Missing, s.State);
        Assert.False(string.IsNullOrWhiteSpace(s.Reason));
        Assert.Equal(JsonValueKind.Object, s.Snapshot().ValueKind);
        Assert.Equal(StoreWrite.Done, new StoreWrite(true, null));
        Assert.Throws<ArgumentException>(() => StoreWrite.Failed(""));
    }

    sealed class MemoryStores : IStores
    {
        readonly Dictionary<string, MemoryStore> _open = new(StringComparer.OrdinalIgnoreCase);
        public IStore Open(string relativePath) =>
            _open.TryGetValue(relativePath, out var s) ? s : _open[relativePath] = new MemoryStore(relativePath);
    }

    sealed class MemoryStore : IStore
    {
        JsonObject _doc = new();
        readonly List<Action<IStore>> _changed = new();

        public MemoryStore(string name) { Name = name; }

        public string Name { get; }
        public ReadState State { get; private set; } = ReadState.Missing;
        public string? Reason { get; private set; } = "no file yet: a fresh profile";

        public void Load(string json)
        {
            _doc = JsonNode.Parse(json)!.AsObject();
            State = ReadState.Ok; Reason = null;
            foreach (var fn in _changed.ToArray()) fn(this);
        }

        public JsonElement Snapshot() => Element(_doc.ToJsonString());

        public bool TryGet(string key, out JsonElement value)
        {
            if (_doc.TryGetPropertyValue(key, out var node)) { value = Element(node?.ToJsonString() ?? "null"); return true; }
            value = default;
            return false;
        }

        public ValueTask<StoreWrite> SetAsync(string key, JsonNode? value)
        {
            _doc[key] = value?.DeepClone();
            return ValueTask.FromResult(StoreWrite.Done);
        }

        public ValueTask<StoreWrite> RemoveAsync(string key)
        {
            _doc.Remove(key);
            return ValueTask.FromResult(StoreWrite.Done);
        }

        public ValueTask<StoreWrite> UpdateAsync(Action<JsonObject> edit)
        {
            var copy = _doc.DeepClone().AsObject();
            edit(copy);
            _doc = copy;
            return ValueTask.FromResult(StoreWrite.Done);
        }

        public IDisposable OnChanged(Action<IStore> handler)
        {
            _changed.Add(handler);
            return new Undo(() => _changed.Remove(handler));
        }

        static JsonElement Element(string json)
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.Clone();
        }
    }

    sealed class Undo : IDisposable
    {
        Action? _undo;
        public Undo(Action undo) { _undo = undo; }
        public void Dispose() { var u = _undo; _undo = null; u?.Invoke(); }
    }
}
