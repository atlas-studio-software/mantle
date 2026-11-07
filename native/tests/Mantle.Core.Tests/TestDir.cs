using System;
using System.Collections.Generic;
using System.IO;
using Mantle.Core.Logging;

namespace Mantle.Core.Tests;

/// A fresh folder under %TEMP%, removed at the end of the test.
public sealed class TestDir : IDisposable
{
    public string Path { get; }

    public TestDir(string tag = "t")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mantle-native-tests", tag + "-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string File(string rel) => System.IO.Path.Combine(Path, rel);

    public string Write(string rel, string text)
    {
        string p = File(rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, text);
        return p;
    }

    /// Electron's own way of saving: write a .tmp beside the file, then rename it over the file.
    public void WriteLikeElectron(string rel, string text)
    {
        string p = File(rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p + ".tmp", text);
        System.IO.File.Move(p + ".tmp", p, overwrite: true);
    }

    /// Copy the synthetic Electron-shaped profile (Fixtures/profile) here.
    public void CopyFixtureProfile()
    {
        string src = System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "profile");
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories)) {
            string rel = System.IO.Path.GetRelativePath(src, f);
            string dst = File(rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst)!);
            System.IO.File.Copy(f, dst, overwrite: true);
        }
    }

    public void Dispose()
    {
        try {
            foreach (var f in Directory.GetFiles(Path, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        } catch (Exception) { /* a leftover temp folder is not a test failure */ }
    }
}

/// A log sink that keeps the lines.
public sealed class ListLog : ILogSink
{
    readonly List<string> _lines = new();
    public void Line(string text) { lock (_lines) _lines.Add(text); }
    public string[] Lines { get { lock (_lines) return _lines.ToArray(); } }
    public string All => string.Join("\n", Lines);
}
