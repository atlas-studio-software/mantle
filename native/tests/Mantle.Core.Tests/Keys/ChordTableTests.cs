using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Mantle.Core.Keys;
using Xunit;

namespace Mantle.Core.Tests.Keys;

// lib/features.test.js: "the chord table matches the sidecar protocol (sidecar/AltTab.cs
// KeyMachine.ChordNames, same order)" and "the observed-key table matches the sidecar protocol".
// The hook sends and receives these names, so the two tables must be the same list in the same order.
public class ChordTableTests
{
    static string AltTabSource()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, "sidecar", "AltTab.cs");
            if (File.Exists(f)) return File.ReadAllText(f);
        }
        throw new Xunit.Sdk.XunitException("sidecar/AltTab.cs was not found above the test folder, so the chord table could not be checked against it");
    }

    static string[] Names(string src, string field)
    {
        var m = Regex.Match(src, field + @"\s*=\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline);
        Assert.True(m.Success, $"{field} was not found in sidecar/AltTab.cs");
        return Regex.Matches(m.Groups["body"].Value, "\"([^\"]+)\"").Select(x => x.Groups[1].Value).ToArray();
    }

    [Fact]
    public void TheChordTableMatchesTheSidecarProtocol_SameOrder() =>
        Assert.Equal(Names(AltTabSource(), "ChordNames"), ChordTable.Chords);

    [Fact]
    public void TheObservedKeyTableMatchesTheSidecarProtocol() =>
        Assert.Equal(Names(AltTabSource(), "KeyNames"), ChordTable.ObservedKeys);

    [Fact]
    public void TheBareVolumeKeysAreChordNames()
    {
        Assert.True(ChordTable.IsChord("VolumeUp") && ChordTable.IsChord("VolumeDown") && ChordTable.IsChord("VolumeMute"));
        Assert.False(ChordTable.IsChord("Win+Q"));
        Assert.Equal(new[] { "Win+S", "Win+Tab" }, ChordTable.InTableOrder(new[] { "Win+Tab", "Win+S" }, ChordTable.Chords));
    }
}
