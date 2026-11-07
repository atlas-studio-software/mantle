using System;
using System.IO;
using System.Text;

namespace Mantle.Core.Features.Pinned;

/// <summary>
/// The fallback MS-SHLLINK reader (lib/pinned.js parseLnkBuffer / parseLnkFile, BAR-267): file targets only. It
/// deliberately does not walk the ExtraData property store, so a Store app's AUMID comes back null and the caller
/// says the sidecar is needed. Anything malformed throws with plain words, which become the item's reason.
/// </summary>
public static class LnkFile
{
    public sealed record Parsed(string? Target, string Args, string Workdir, string Name, string RelativePath, string? IconPath, int IconIndex);

    const uint IdList = 0x01, LinkInfo = 0x02, HasName = 0x04, RelPath = 0x08, WorkDir = 0x10, HasArgs = 0x20, IconLoc = 0x40, Unicode = 0x80;
    static readonly byte[] Clsid = { 0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46 };

    static uint U32(byte[] b, int at) => BitConverter.ToUInt32(b, at);
    static ushort U16(byte[] b, int at) => BitConverter.ToUInt16(b, at);

    static string AnsiZ(byte[] b, int at)
    {
        if (at < 0 || at >= b.Length) return "";
        int end = at; while (end < b.Length && b[end] != 0) end++;
        return Encoding.Latin1.GetString(b, at, end - at);
    }

    static string Utf16Z(byte[] b, int at)
    {
        if (at < 0 || at + 1 >= b.Length) return "";
        int end = at; while (end + 1 < b.Length && !(b[end] == 0 && b[end + 1] == 0)) end += 2;
        return Encoding.Unicode.GetString(b, at, end - at);
    }

    public static Parsed Parse(byte[] buf)
    {
        if (buf == null || buf.Length < 0x4C) throw new InvalidDataException("the file is too small to be a shortcut (" + (buf?.Length ?? 0) + " bytes)");
        uint headerSize = U32(buf, 0);
        if (headerSize != 0x4C) throw new InvalidDataException("not a shortcut: its header says " + headerSize + " bytes, a .lnk header is 76");
        for (int i = 0; i < 16; i++) if (buf[4 + i] != Clsid[i]) throw new InvalidDataException("not a shortcut: the class id in the header is wrong");
        uint flags = U32(buf, 0x14);
        bool uni = (flags & Unicode) != 0;
        int pos = 0x4C;
        if ((flags & IdList) != 0) {
            if (pos + 2 > buf.Length) throw new InvalidDataException("the shortcut ends before its target list");
            pos += 2 + U16(buf, pos);
            if (pos > buf.Length) throw new InvalidDataException("the shortcut is cut off inside its target list");
        }
        string? target = null;
        if ((flags & LinkInfo) != 0) {
            if (pos + 8 > buf.Length) throw new InvalidDataException("the shortcut ends before its link info");
            int start = pos;
            uint size = U32(buf, start), hdr = U32(buf, start + 4);
            if (size < 0x1C || start + (long)size > buf.Length) throw new InvalidDataException("the shortcut has a damaged link info block");
            uint liFlags = U32(buf, start + 8);
            if ((liFlags & 0x01) != 0) {
                string b = "", s = "";
                if (hdr >= 0x24) {
                    uint ub = U32(buf, start + 0x1C), us = U32(buf, start + 0x20);
                    if (ub != 0) b = Utf16Z(buf, start + (int)ub);
                    if (us != 0) s = Utf16Z(buf, start + (int)us);
                }
                if (b.Length == 0) {
                    uint ab = U32(buf, start + 0x10), asf = U32(buf, start + 0x18);
                    if (ab != 0) b = AnsiZ(buf, start + (int)ab);
                    if (s.Length == 0 && asf != 0) s = AnsiZ(buf, start + (int)asf);
                }
                if (b.Length > 0) target = b + s;
            }
            pos = start + (int)size;
        }
        string Read()
        {
            if (pos + 2 > buf.Length) throw new InvalidDataException("the shortcut is cut off inside its text");
            int count = U16(buf, pos); pos += 2;
            int bytes = count * (uni ? 2 : 1);
            if (pos + bytes > buf.Length) throw new InvalidDataException("the shortcut is cut off inside its text");
            string v = uni ? Encoding.Unicode.GetString(buf, pos, bytes) : Encoding.Latin1.GetString(buf, pos, bytes);
            pos += bytes;
            return v;
        }
        string name = (flags & HasName) != 0 ? Read() : "";
        string rel = (flags & RelPath) != 0 ? Read() : "";
        string wd = (flags & WorkDir) != 0 ? Read() : "";
        string args = (flags & HasArgs) != 0 ? Read() : "";
        string iconLoc = (flags & IconLoc) != 0 ? Read() : "";
        var icon = PinnedReader.SplitIconLocation(iconLoc);
        return new Parsed(target, args, wd, name, rel, icon.IconPath, icon.IconIndex);
    }

    /// The second chance for a shortcut with no LinkInfo: RELATIVE_PATH against the .lnk's own folder.
    public static Parsed ParseFile(string fullPath)
    {
        var p = Parse(File.ReadAllBytes(fullPath));
        if (string.IsNullOrEmpty(p.Target) && p.RelativePath.Length > 0) {
            try { p = p with { Target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath) ?? "", p.RelativePath)) }; } catch (Exception) { }
        }
        return p;
    }
}
