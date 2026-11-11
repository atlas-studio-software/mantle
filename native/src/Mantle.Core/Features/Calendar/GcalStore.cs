using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Mantle.Core.Data;

namespace Mantle.Core.Features.Calendar;

/// <summary>
/// What Google Calendar keeps on this PC, all of it under &lt;userData&gt;\gcal\ (lib/features/_gcal-store.js):
///   token.bin   the sign-in (refresh + access token), ENCRYPTED for the user's Windows account; never plain text: no
///               encryption = no sign-in (privacy claim 3);
///   client.bin  the client id / secret copied out of the chosen Google client file, also encrypted;
///   cache.json  the recent events and the calendar list, so the flyout opens from disk with no network (claim 4).
/// Sign out deletes token.bin AND cache.json (claim 6); client.bin is the user's own app registration, kept.
/// Every write is temp file + rename through <see cref="SafeFile"/> (the one write gate); a delete also removes any
/// "&lt;name&gt;.&lt;pid&gt;.tmp" a crash left.
///
/// The encryption is Electron's own format whenever Electron's key is there, so both apps read the same files in the
/// fallback period (PLAN 3.7(a)): Chromium's "v10" AES-256-GCM with the key from "Local State" (os_crypt.encrypted_key,
/// itself DPAPI-protected for the current user). Without that key (a profile Electron never ran in) it is plain
/// DPAPI, current user. Both are read back.
/// </summary>
public sealed class GcalStore : IGcalStore
{
    readonly SafeFile _files;
    readonly string _localState;
    readonly Func<bool> _canEncrypt;

    public GcalStore(string dir, SafeFile files, string? localStatePath = null, Func<bool>? canEncrypt = null)
    {
        Dir = dir; _files = files;
        _localState = localStatePath ?? Path.Combine(Path.GetDirectoryName(dir) ?? dir, "Local State");
        _canEncrypt = canEncrypt ?? Dpapi.Available;
    }

    public string Dir { get; }
    string F(string n) => Path.Combine(Dir, n);

    public bool CanEncrypt() { try { return _canEncrypt(); } catch (Exception) { return false; } }

    void WriteAtomic(string name, byte[] data)
    {
        string? no = _files.WriteAtomic(F(name), data, F(name + "." + Environment.ProcessId + ".tmp"));
        if (no != null) throw new GcalStoreError(CodeOf(no), no);
    }

    void PutSecret(string name, JsonObject obj)
    {
        if (!CanEncrypt()) throw new GcalError("other", "Windows encryption (DPAPI) is not available, so Mantle will not store a Google sign-in");
        WriteAtomic(name, Protect(Encoding.UTF8.GetBytes(obj.ToJsonString())));
    }

    /// The object; null when there is none; or a <see cref="GcalSecret"/> with Broken set when it cannot be read back.
    GcalSecret GetSecret(string name)
    {
        byte[] buf;
        try { buf = File.ReadAllBytes(F(name)); }
        catch (Exception e) { return SafeFile.Code(e) == "ENOENT" ? new GcalSecret(null, null) : new GcalSecret(null, "it could not be read (" + SafeFile.Code(e) + ")"); }
        if (!CanEncrypt()) return new GcalSecret(null, "Windows encryption (DPAPI) is not available");
        try { return new GcalSecret(JsonNode.Parse(Encoding.UTF8.GetString(Unprotect(buf))) as JsonObject, null); }
        catch (Exception) { return new GcalSecret(null, "it could not be decrypted (made under another Windows account?)"); }
    }

    // ------------------------------------------------------------------ the two formats

    static readonly byte[] V10 = "v10"u8.ToArray();

    byte[] Protect(byte[] plain)
    {
        var key = ElectronKey();
        if (key == null) return Dpapi.Protect(plain);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ct = new byte[plain.Length]; var tag = new byte[16];
        using (var g = new AesGcm(key, 16)) g.Encrypt(nonce, plain, ct, tag);
        var o = new byte[3 + 12 + ct.Length + 16];
        V10.CopyTo(o, 0); nonce.CopyTo(o, 3); ct.CopyTo(o, 15); tag.CopyTo(o, 15 + ct.Length);
        return o;
    }

    byte[] Unprotect(byte[] buf)
    {
        if (buf.Length >= 3 + 12 + 16 && buf.AsSpan(0, 3).SequenceEqual(V10)) {
            var key = ElectronKey() ?? throw new CryptographicException("no Electron key");
            var nonce = buf.AsSpan(3, 12); int n = buf.Length - 3 - 12 - 16;
            var plain = new byte[n];
            using var g = new AesGcm(key, 16);
            g.Decrypt(nonce, buf.AsSpan(15, n), buf.AsSpan(15 + n, 16), plain);
            return plain;
        }
        return Dpapi.Unprotect(buf);
    }

    /// Electron's safeStorage key: Local State's os_crypt.encrypted_key, base64, "DPAPI" + a DPAPI blob of 32 bytes.
    /// Null when there is no such file or key (read only: native never writes Local State).
    byte[]? ElectronKey()
    {
        try {
            if (!File.Exists(_localState)) return null;
            var j = JsonNode.Parse(File.ReadAllText(_localState));
            string? b64 = GcalCore.Str(j?["os_crypt"]?["encrypted_key"]);
            if (string.IsNullOrEmpty(b64)) return null;
            var raw = Convert.FromBase64String(b64);
            if (raw.Length < 6 || Encoding.ASCII.GetString(raw, 0, 5) != "DPAPI") return null;
            var key = Dpapi.Unprotect(raw[5..]);
            return key.Length == 32 ? key : null;
        } catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ the files

    /// Deleting a file also deletes any "&lt;name&gt;.&lt;pid&gt;.tmp" a crash left, so Sign out leaves no copy anywhere.
    /// Missing is success; a failure throws with Node's code (EPERM, EBUSY, ...).
    public void Remove(string name)
    {
        string[] names;
        try { names = Directory.Exists(Dir) ? Directory.GetFiles(Dir) : Array.Empty<string>(); } catch (Exception) { names = Array.Empty<string>(); }
        foreach (var full in names) {
            string n = Path.GetFileName(full);
            if (n.StartsWith(name + ".", StringComparison.Ordinal) && n.EndsWith(".tmp", StringComparison.Ordinal)) {
                if (_files.Remove(full) is { } e) throw new GcalStoreError(CodeOf(e), e);
            }
        }
        if (_files.Remove(F(name)) is { } why) throw new GcalStoreError(CodeOf(why), why);
    }

    /// Node's code inside SafeFile's words ("the new copy could not be written (EPERM)" -> EPERM); the gate's refusal keeps its words.
    static string CodeOf(string words) => System.Text.RegularExpressions.Regex.Match(words, @"\b(E[A-Z]{2,})\b") is { Success: true } m ? m.Groups[1].Value : words;

    public GcalSecret GetToken() => GetSecret("token.bin");
    public void PutToken(JsonObject t) => PutSecret("token.bin", t);
    public void DeleteToken() => Remove("token.bin");
    public GcalSecret GetClient() => GetSecret("client.bin");
    public void PutClient(GcalClient c) => PutSecret("client.bin", new JsonObject { ["clientId"] = c.ClientId, ["clientSecret"] = c.ClientSecret, ["project"] = c.Project });

    public JsonObject? GetCache()
    {
        try {
            var j = JsonNode.Parse(File.ReadAllText(F("cache.json"))) as JsonObject;
            return j != null && GcalCore.Num(j["v"]) == 1 && j["events"] is JsonArray ? j : null;
        } catch (Exception) { return null; }
    }

    public void PutCache(JsonObject c)
    {
        var o = new JsonObject { ["v"] = 1 };
        foreach (var kv in c) if (kv.Key != "v") o[kv.Key] = kv.Value?.DeepClone();
        WriteAtomic("cache.json", Encoding.UTF8.GetBytes(o.ToJsonString()));
    }

    public void DeleteCache() => Remove("cache.json");
    public bool Has(string name) => File.Exists(F(name));
}

/// A secret read back: Value, or nothing (both null), or Broken = why it could not be read.
public sealed record GcalSecret(JsonObject? Value, string? Broken);

/// A store write or delete that failed; Code is Node's (ENOENT, EPERM, EBUSY ...) or the gate's words.
public sealed class GcalStoreError : Exception
{
    public GcalStoreError(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}

/// The store as the engine sees it (a test gives a fake).
public interface IGcalStore
{
    bool CanEncrypt();
    GcalSecret GetToken();
    void PutToken(JsonObject t);
    void DeleteToken();
    GcalSecret GetClient();
    void PutClient(GcalClient c);
    JsonObject? GetCache();
    void PutCache(JsonObject c);
    void DeleteCache();
}

/// <summary>Windows DPAPI for the current user (CryptProtectData / CryptUnprotectData, no entropy, no UI).</summary>
public static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob { public int Size; public IntPtr Data; }

    const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptProtectData(ref Blob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob dataOut);
    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);

    public static bool Available()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try { var p = Protect(new byte[] { 1, 2, 3 }); return Unprotect(p).AsSpan().SequenceEqual(new byte[] { 1, 2, 3 }); }
        catch (Exception) { return false; }
    }

    public static byte[] Protect(byte[] plain) => Run(plain, true);
    public static byte[] Unprotect(byte[] blob) => Run(blob, false);

    static byte[] Run(byte[] input, bool protect)
    {
        var h = GCHandle.Alloc(input, GCHandleType.Pinned);
        try {
            var inb = new Blob { Size = input.Length, Data = h.AddrOfPinnedObject() };
            Blob outb;
            bool ok = protect
                ? CryptProtectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outb)
                : CryptUnprotectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outb);
            if (!ok) throw new CryptographicException("DPAPI refused (" + Marshal.GetLastWin32Error() + ")");
            try { var o = new byte[outb.Size]; Marshal.Copy(outb.Data, o, 0, outb.Size); return o; }
            finally { LocalFree(outb.Data); }
        } finally { h.Free(); }
    }
}
