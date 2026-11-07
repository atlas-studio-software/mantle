using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Mantle.Core.Features.LauncherIndex;

/// <summary>
/// The minimal MS-SHLLINK reader of lib/pinned.js#parseLnkBuffer / #parseLnkFile, ported for the launcher's
/// stand-in check (what a "Task Manager" or "Settings" shortcut runs). It reads the target (LinkInfo, else
/// the relative path against the .lnk's folder), the arguments and the icon location, and nothing else.
/// Malformed input throws <see cref="InvalidDataException"/> with Electron's plain words.
/// </summary>
public static class LnkFile
{
    const uint F_ID_LIST = 0x01, F_LINK_INFO = 0x02, F_NAME = 0x04, F_RELATIVE_PATH = 0x08, F_WORKING_DIR = 0x10,
        F_ARGUMENTS = 0x20, F_ICON_LOCATION = 0x40, F_UNICODE = 0x80;

    static readonly byte[] LinkClsid = [0x01, 0x14, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    public sealed record Parsed(string? Target, string Args, string WorkDir, string Name, string RelativePath, string? IconPath, int IconIndex);

    public static Parsed ReadFile(string fullPath)
    {
        var p = Parse(File.ReadAllBytes(fullPath));
        if (p.Target == null && p.RelativePath.Length > 0)
        {
            try { p = p with { Target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath) ?? "", p.RelativePath)) }; }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { /* keep null */ }
        }
        return p;
    }

    static readonly Encoding Latin1 = Encoding.Latin1;

    static string AnsiZ(byte[] b, int at)
    {
        if (at < 0 || at >= b.Length) return "";
        int end = at; while (end < b.Length && b[end] != 0) end++;
        return Latin1.GetString(b, at, end - at);
    }

    static string Utf16Z(byte[] b, int at)
    {
        if (at < 0 || at + 1 >= b.Length) return "";
        int end = at; while (end + 1 < b.Length && !(b[end] == 0 && b[end + 1] == 0)) end += 2;
        return Encoding.Unicode.GetString(b, at, end - at);
    }

    static uint U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at, 4));
    static ushort U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at, 2));

    public static Parsed Parse(byte[] buf)
    {
        if (buf.Length < 0x4C) throw new InvalidDataException("the file is too small to be a shortcut (" + buf.Length + " bytes)");
        uint headerSize = U32(buf, 0);
        if (headerSize != 0x4C) throw new InvalidDataException("not a shortcut: its header says " + headerSize + " bytes, a .lnk header is 76");
        if (!buf.AsSpan(4, 16).SequenceEqual(LinkClsid)) throw new InvalidDataException("not a shortcut: the class id in the header is wrong");
        uint flags = U32(buf, 0x14);
        bool unicode = (flags & F_UNICODE) != 0;
        int pos = 0x4C;
        if ((flags & F_ID_LIST) != 0)
        {
            if (pos + 2 > buf.Length) throw new InvalidDataException("the shortcut ends before its target list");
            pos += 2 + U16(buf, pos);
            if (pos > buf.Length) throw new InvalidDataException("the shortcut is cut off inside its target list");
        }
        string? target = null;
        if ((flags & F_LINK_INFO) != 0)
        {
            if (pos + 8 > buf.Length) throw new InvalidDataException("the shortcut ends before its link info");
            int start = pos;
            long size = U32(buf, start), hdrSize = U32(buf, start + 4);
            if (size < 0x1C || start + size > buf.Length) throw new InvalidDataException("the shortcut has a damaged link info block");
            uint liFlags = U32(buf, start + 8);
            if ((liFlags & 1) != 0)
            {
                string bas = "", suffix = "";
                if (hdrSize >= 0x24)
                {
                    uint uBase = U32(buf, start + 0x1C), uSuffix = U32(buf, start + 0x20);
                    if (uBase != 0) bas = Utf16Z(buf, start + (int)uBase);
                    if (uSuffix != 0) suffix = Utf16Z(buf, start + (int)uSuffix);
                }
                if (bas.Length == 0)
                {
                    uint aBase = U32(buf, start + 0x10), aSuffix = U32(buf, start + 0x18);
                    if (aBase != 0) bas = AnsiZ(buf, start + (int)aBase);
                    if (suffix.Length == 0 && aSuffix != 0) suffix = AnsiZ(buf, start + (int)aSuffix);
                }
                if (bas.Length > 0) target = bas + suffix;
            }
            pos = start + (int)size;
        }
        string ReadString()
        {
            if (pos + 2 > buf.Length) throw new InvalidDataException("the shortcut is cut off inside its text");
            int count = U16(buf, pos); pos += 2;
            int bytes = count * (unicode ? 2 : 1);
            if (pos + bytes > buf.Length) throw new InvalidDataException("the shortcut is cut off inside its text");
            string s = unicode ? Encoding.Unicode.GetString(buf, pos, bytes) : Latin1.GetString(buf, pos, bytes);
            pos += bytes;
            return s;
        }
        string name = (flags & F_NAME) != 0 ? ReadString() : "";
        string rel = (flags & F_RELATIVE_PATH) != 0 ? ReadString() : "";
        string wd = (flags & F_WORKING_DIR) != 0 ? ReadString() : "";
        string args = (flags & F_ARGUMENTS) != 0 ? ReadString() : "";
        string iconLoc = (flags & F_ICON_LOCATION) != 0 ? ReadString() : "";
        var (iconPath, iconIndex) = SplitIconLocation(iconLoc);
        return new Parsed(target, args, wd, name, rel, iconPath, iconIndex);
    }

    static readonly Regex IconLoc = new(@"^(.*[^,]),\s*(-?\d+)$", RegexOptions.CultureInvariant);

    /// lib/pinned.js#splitIconLocation: "C:\\x\\shell32.dll,15" -> (path, 15). A negative index is legal.
    public static (string? Path, int Index) SplitIconLocation(string? s)
    {
        string str = (s ?? "").Trim();
        if (str.Length == 0) return (null, 0);
        var m = IconLoc.Match(str);
        if (m.Success && int.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out int idx))
            return (m.Groups[1].Value.Trim(), idx);
        return (str, 0);
    }
}
