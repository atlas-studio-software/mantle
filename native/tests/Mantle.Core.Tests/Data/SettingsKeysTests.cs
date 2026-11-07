using System.Linq;
using Mantle.Core.Data;
using Xunit;

namespace Mantle.Core.Tests.Data;

/// The settings key index (settings-mantle.md) as data: every file is one a native store opens with Electron's format.
public class SettingsKeysTests
{
    [Fact]
    public void Every_settings_file_in_the_index_is_a_file_a_store_may_open()
    {
        Assert.Equal(20, SettingsKeys.All.Count);
        foreach (var k in SettingsKeys.All) {
            string file = k.File.Replace("<feature>", "gamemode");
            Assert.True(DataFileSpecs.For(file) != null, k.Key + " lives in " + k.File + ", which no store may open");
        }
        Assert.Equal(new[] { "mode" }, SettingsKeys.In("theme.json").Select(k => k.Key));
        Assert.Equal(new[] { "lookSync", "accent", "glass" }, SettingsKeys.In("look-sync.json").Select(k => k.Key));
        Assert.Equal(new[] { "<feature>.<option>", "gamemode.auto", "gamemode.liftSaver", "gamemode.boost" }, SettingsKeys.In("features/gamemode.json").Select(k => k.Key));
        Assert.Equal(new[] { "keepReady.settings", "keepReady.taskmgr", "keepReady.files" }, SettingsKeys.In("keep-ready.json").Select(k => k.Key));
        Assert.Empty(SettingsKeys.In(@"..\x.json"));
    }
}
