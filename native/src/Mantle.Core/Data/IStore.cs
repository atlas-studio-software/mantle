using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Mantle.Core.Data;

// A writable JSON settings file in the profile (PLAN.md decision A10, docs/data-files.md (a)): the
// same path, the same shape, atomic tmp-and-rename writes, the ".unreadable" aside rule, and every key
// this build does not know kept exactly as it was, so the user's settings carry over and a rollback to
// Electron keeps working. Contract only: native-data-look implements it next to the read-only readers
// already in this folder (ReadResult.cs, Readers.cs, MantleData.cs).
//
// Threading: host-thread only. The in-memory document is the truth: TryGet sees a Set at once, and
// the returned task completes when the file write has finished (it may run off the host thread, but
// it never reorders two writes to one file). OnChanged handlers run on the host thread.

/// <summary>How a write ended: Ok, or plain words for why the file on disk was not changed.</summary>
public sealed record StoreWrite(bool Ok, string? Reason)
{
    public static readonly StoreWrite Done = new(true, null);

    public static StoreWrite Failed(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("a failed write needs its reason in plain words", nameof(reason));
        return new(false, reason);
    }
}

/// <summary>One JSON object file in the profile.</summary>
public interface IStore
{
    /// The file's name as the log shows it, relative to the profile (<c>features\clipboard.json</c>), never a full path.
    string Name { get; }

    /// What the last load found (the same states as the readers: Ok, Missing, Corrupt, ...).
    ReadState State { get; }

    /// Plain words for every state but a clean Ok (for example, that an unreadable file was set aside).
    string? Reason { get; }

    /// The whole document as it is now: always an object (<c>{}</c> when the file is missing or unusable). Detached.
    JsonElement Snapshot();

    /// One top-level key's value (detached), or false when the key is absent.
    bool TryGet(string key, out JsonElement value);

    /// <summary>Set one top-level key (a null value is JSON null; use <see cref="RemoveAsync"/> to delete).</summary>
    /// <remarks>Every other key, known or not, is written back unchanged. A file that could not be
    /// read is moved aside as <c>&lt;name&gt;.unreadable</c> before the first write, never overwritten.
    /// The value is copied, so the caller may reuse the node.</remarks>
    ValueTask<StoreWrite> SetAsync(string key, JsonNode? value);

    /// Delete one top-level key (nothing is written when it was absent).
    ValueTask<StoreWrite> RemoveAsync(string key);

    /// Several changes as ONE write: <paramref name="edit"/> gets a copy of the whole document and changes
    /// it in place. Keys it leaves alone stay exactly as they were.
    ValueTask<StoreWrite> UpdateAsync(Action<JsonObject> edit);

    /// The file changed on disk under us (Electron, Helm, the user's editor) and was read again.
    IDisposable OnChanged(Action<IStore> handler);
}

/// <summary>Where stores come from: one instance per file for the life of the process.</summary>
public interface IStores
{
    /// <summary>The store for a file under the profile folder (<c>%APPDATA%\mantle</c> live, the test profile in <c>--test</c>).</summary>
    /// <param name="relativePath">For example <c>theme.json</c> or <c>features\clipboard.json</c>; never rooted, never <c>..</c>.</param>
    IStore Open(string relativePath);
}
