using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Mantle.Core.Logging;
using Xunit;

namespace Mantle.Core.Tests.Lifecycle;

/// UiSlice (lane native-r8-startup): a plain call while the stall meter is off; named, timed and logged while it is on.
/// It is process-wide, so each assertion looks only at this test's own slice names.
public class UiSliceTests
{
    [Fact]
    public void Off_it_is_the_call_alone_and_on_a_slow_slice_is_named_and_logged_and_the_name_is_seen_while_it_runs()
    {
        var lines = new List<string>();
        int ran = 0;
        UiSlice.Run("uislice-test off", null, () => ran++);
        UiSlice.Run<int>("uislice-test off arg", null, n => ran += n, 2);
        Assert.Equal(3, ran);

        UiSlice.Begin(l => { lock (lines) lines.Add(l); });
        try {
            string? inside = null;
            UiSlice.Run("uislice-test slow", "a who", () => { inside = UiSlice.Now; Thread.Sleep(80); });
            UiSlice.Run("uislice-test fast", null, () => { });
            UiSlice.Run<int>("uislice-test arg", "w", n => ran += n, 5);
            Assert.Equal("uislice-test slow (a who)", inside);
            Assert.Equal(8, ran);
            lock (lines) {
                Assert.Contains(lines, l => l.StartsWith("ui slice: uislice-test slow (a who) held the UI thread ", System.StringComparison.Ordinal) && l.EndsWith(" ms", System.StringComparison.Ordinal));
                Assert.DoesNotContain(lines, l => l.Contains("uislice-test fast", System.StringComparison.Ordinal) || l.Contains("uislice-test arg", System.StringComparison.Ordinal));
            }
        } finally {
            UiSlice.End();
        }
        Assert.False(UiSlice.On);
        UiSlice.Run("uislice-test off again", null, () => Thread.Sleep(60));
        lock (lines) Assert.DoesNotContain(lines, l => l.Contains("uislice-test off", System.StringComparison.Ordinal));
    }
}
