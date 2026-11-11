using System.Collections.Generic;
using System.Text.Json;
using Mantle.Core.Features.Badges;
using Xunit;

namespace Mantle.Core.Tests.Badges;

/// <summary>lib/helm-badge.test.js, the pure half (parse, match, words, sources). No real Helm file is read.</summary>
public class HelmBadgeTests
{
    static bool Alive(int pid) => pid == 4242;

    [Fact]
    public void The_badge_file_parse_counts_waiting_plus_errors_and_says_why_there_is_no_count()
    {
        Assert.Equal(new BadgeReading(0, 0, 0, "not running"), HelmBadge.Parse(null, Alive));
        Assert.Equal(new BadgeReading(0, 0, 0, "its badge file could not be read"), HelmBadge.Parse("{nope", Alive));
        Assert.Equal(new BadgeReading(0, 0, 0, "its badge file could not be read"), HelmBadge.Parse("[1]", Alive));
        Assert.Equal(new BadgeReading(3, 2, 1, ""), HelmBadge.Parse("{\"v\":1,\"waiting\":2,\"errors\":1,\"pid\":4242}", Alive));
        Assert.Equal(new BadgeReading(5, 2, 1, ""), HelmBadge.Parse("{\"count\":5,\"waiting\":2,\"errors\":1}", Alive));
        Assert.Equal(new BadgeReading(2, 2, 0, ""), HelmBadge.Parse("{\"count\":-4,\"waiting\":2.7,\"errors\":\"x\"}", Alive));   // junk numbers = 0
        Assert.Equal(new BadgeReading(0, 0, 0, "not running"), HelmBadge.Parse("{\"waiting\":2,\"pid\":99}", Alive));          // its writer is gone
        Assert.Equal(new BadgeReading(0, 0, 0, ""), HelmBadge.Parse("{\"waiting\":0}", Alive));
    }

    [Fact]
    public void A_bare_name_matches_any_folder_and_a_full_path_only_itself()
    {
        var bare = new BadgeSource("helm.exe", @"C:\x\badge.json", "Helm");
        Assert.True(HelmBadge.Matches(bare, @"C:\Users\u\AppData\Local\Programs\Helm\Helm.exe"));
        Assert.True(HelmBadge.Matches(bare, "HELM.EXE"));
        Assert.False(HelmBadge.Matches(bare, @"C:\x\overhelm.exe"));
        Assert.False(HelmBadge.Matches(bare, null));
        var full = new BadgeSource("C:/Apps/Helm/helm.exe", @"C:\x\badge.json", "Helm");
        Assert.True(HelmBadge.Matches(full, @"c:\apps\helm\HELM.exe"));
        Assert.False(HelmBadge.Matches(full, @"D:\Apps\Helm\helm.exe"));
    }

    [Fact]
    public void Words_disc_text_and_ink()
    {
        Assert.Equal("2 need you: 1 waiting, 1 stopped on an error", HelmBadge.Describe(new BadgeReading(2, 1, 1, "")));
        Assert.Equal("1 needs you: 1 waiting", HelmBadge.Describe(new BadgeReading(1, 1, 0, "")));
        Assert.Equal("4 need you", HelmBadge.Describe(new BadgeReading(4, 0, 0, "")));
        Assert.Equal("", HelmBadge.Describe(new BadgeReading(0, 0, 0, "not running")));
        Assert.Equal("9", HelmBadge.DiscText(9));
        Assert.Equal("9+", HelmBadge.DiscText(10));
        Assert.Equal("", HelmBadge.DiscText(0));
        Assert.True(HelmBadge.DiscWarn(new BadgeReading(2, 1, 1, "")));
        Assert.False(HelmBadge.DiscWarn(new BadgeReading(2, 2, 0, "")));
    }

    [Fact]
    public void Sources_default_to_Helms_file_or_the_test_file_and_custom_ones_are_cleaned()
    {
        var env = new Dictionary<string, string?> { ["APPDATA"] = @"C:\fake\Roaming" };
        Assert.Equal(new BadgeSource("helm.exe", @"C:\fake\Roaming\Helm\badge.json", "Helm"), HelmBadge.DefaultSources(k => env.GetValueOrDefault(k))[0]);
        env["MANTLE_HELM_BADGE_FILE"] = @"C:\tmp\fixture-badge.json";
        Assert.Equal(@"C:\tmp\fixture-badge.json", HelmBadge.DefaultSources(k => env.GetValueOrDefault(k))[0].File);
        using var doc = JsonDocument.Parse("[{\"exe\":\" Quartz.exe \",\"file\":\"C:\\\\q\\\\b.json\"},{\"exe\":\"x.exe\",\"file\":\"relative.json\"},{\"exe\":\"\",\"file\":\"C:\\\\a.json\"},7,{\"exe\":\"y.exe\",\"file\":\"C:\\\\y.json\",\"label\":\" Why \"}]");
        var s = HelmBadge.CleanSources(doc.RootElement);
        Assert.Equal(2, s.Count);
        Assert.Equal(new BadgeSource("Quartz.exe", @"C:\q\b.json", "Quartz"), s[0]);
        Assert.Equal("Why", s[1].Label);
    }
}
