using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Toasts;

/// <summary>
/// The record file as lib/features/toasts.js keeps it: <c>&lt;userData&gt;\notif-banners.json</c>, written through
/// <see cref="SafeFile"/> (the ONE write gate: a test or scratch profile, or a --live run that is the only shell)
/// as tmp + rename, with <c>pid</c> and <c>at</c> added so the crash supervisor never puts back what a LIVE Mantle
/// took. A save the gate refuses THROWS, so the owner writes nothing to Windows (write-ahead). The put-back
/// marker files next to it are only read here, never written (BannerRecordFileTests checks no write call
/// skips SafeFile in this folder).
/// </summary>
public sealed class BannerRecordFile : IBannerRecordStore
{
    public const string FileName = "notif-banners.json";

    readonly SafeFile _files;
    readonly int _pid;
    readonly Func<double> _now;

    public BannerRecordFile(string dataRoot, SafeFile files, int? pid = null, Func<double>? now = null)
    {
        Path = System.IO.Path.Combine(dataRoot, FileName);
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _pid = pid ?? Environment.ProcessId;
        _now = now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    public string Path { get; }
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? ".";

    public JsonNode? Load(out bool exists)
    {
        string raw;
        try { raw = File.ReadAllText(Path, Encoding.UTF8); }
        catch (FileNotFoundException) { exists = false; return null; }
        catch (DirectoryNotFoundException) { exists = false; return null; }
        exists = true;
        return JsonNode.Parse(raw);   // throws when torn: the owner says so and takes nothing
    }

    public void Save(BannerRecord record)
    {
        var json = record.ToJson(("pid", JsonValue.Create(_pid)), ("at", BannerRecord.Number(Math.Floor(_now())))).ToJsonString();
        var fail = _files.WriteAtomic(Path, Encoding.UTF8.GetBytes(json), Path + ".tmp");
        if (fail != null) throw new IOException(fail);
    }

    public BackFiles LoadBack() => ReadBackFiles(Folder);

    /// readBackFiles: every marker entry in <paramref name="dir"/>; a missing file gives nothing, a torn one is named.
    public static BackFiles ReadBackFiles(string dir)
    {
        var entries = new List<BackEntry>();
        var bad = new List<string>();
        foreach (var name in BackFiles.FileNames)
        {
            string text;
            try { text = File.ReadAllText(System.IO.Path.Combine(dir ?? ".", name), Encoding.UTF8); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (Exception ex) { bad.Add($"{name}: {Words.First(ex.Message)}"); continue; }
            var (list, why) = BackFiles.Parse(name, text);
            if (why != null) bad.Add(why); else entries.AddRange(list);
        }
        return new BackFiles(entries, bad);
    }
}
