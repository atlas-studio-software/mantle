using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Features.TopBar;
using Xunit;

namespace Mantle.Core.Tests.Features.TopBar;

/// <summary>
/// The top-row hints receiver (lib/toprow-hints.js; its checks in lib/toprow-hints.test.js): the protocol, the 8 KB
/// lines, seq, the sender's window check, light theme, forgetting, the limits, and a real pipe (busy, then free).
/// </summary>
public sealed class TopRowHintsTests
{
    const string Home = @"C:\Users\someone";
    const string QuartzExe = @"C:\Program Files\Quartz\Quartz.exe";
    readonly List<string> _logs = new();
    readonly List<(string Hwnd, string App, string? Top, string? Why)> _fwd = new();
    readonly Dictionary<string, string> _owners = new();   // hwnd -> exe path
    int _checks;
    bool _dark = true;
    long _now = 1_000_000;

    TopRowHints Make(int retryMs = 0, string? pipe = null) => new(new TopRowHintsOptions
    {
        PipeName = pipe ?? "unused-" + Guid.NewGuid().ToString("N"), Home = Home, RetryMs = retryMs,
        Now = () => Interlocked.Read(ref _now), Dark = () => _dark,
        Log = l => { lock (_logs) _logs.Add(l); },
        Forward = (h, a, t, m) => { lock (_fwd) _fwd.Add((h, a, t?.ToJsonString(), m.Why)); },
        ProcOf = h => { Interlocked.Increment(ref _checks); return Task.FromResult(_owners.TryGetValue(h, out var p) ? new HintProc(true, 4242, p) : new HintProc(false, Reason: "there is no such window")); },
    });

    static string Line(string app, string hwnd, long seq, string top) => $"{{\"v\":1,\"app\":\"{app}\",\"hwnd\":\"{hwnd}\",\"seq\":{seq},\"t\":1700000000000,\"top\":{top}}}\n";
    const string Flat = "{\"kind\":\"flat\",\"rgb\":[10,20,30]}";
    static void Send(TopRowHints h, TopRowHints.Conn c, string s) => h.Feed(c, Encoding.UTF8.GetBytes(s));
    static bool Until(Func<bool> f, int ms = 3000) { var sw = Stopwatch.StartNew(); while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(10); } return f(); }
    int Fwds() { lock (_fwd) return _fwd.Count; }
    (string Hwnd, string App, string? Top, string? Why) Last() { lock (_fwd) return _fwd[^1]; }
    int Ignored(TopRowHints h, string why) => h.View()["ignored"]?[why]?.GetValue<int>() ?? 0;

    [Fact]
    public void The_protocol_v1_messages_and_what_is_refused_TOP_363()
    {
        Assert.NotNull(TopRowHints.CheckMessage(JsonNode.Parse(Line("quartz", "131072", 0, Flat))).Msg);
        Assert.Equal("{\"kind\":\"wash\",\"rgb\":[1,2,3],\"alpha\":0.5}", TopRowHints.NormaliseTop(JsonNode.Parse("{\"kind\":\"wash\",\"rgb\":[1,2,3],\"alpha\":0.5}")).Top!.ToJsonString());
        Assert.Equal("{\"kind\":\"none\"}", TopRowHints.NormaliseTop(JsonNode.Parse("{\"kind\":\"none\"}")).Top!.ToJsonString());
        foreach (var (json, why) in new[]
        {
            ("[1]", "not valid JSON"), ("{\"v\":2,\"app\":\"quartz\",\"hwnd\":\"1\",\"seq\":0,\"t\":1,\"top\":" + Flat + "}", "not protocol v1"),
            ("{\"v\":1,\"app\":\"edge\",\"hwnd\":\"1\",\"seq\":0,\"t\":1,\"top\":" + Flat + "}", "unknown app"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":131072,\"seq\":0,\"t\":1,\"top\":" + Flat + "}", "bad hwnd"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"0131072\",\"seq\":0,\"t\":1,\"top\":" + Flat + "}", "bad hwnd"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"1\",\"seq\":-1,\"t\":1,\"top\":" + Flat + "}", "bad seq"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"1\",\"seq\":1.5,\"t\":1,\"top\":" + Flat + "}", "bad seq"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"1\",\"seq\":1,\"t\":\"x\",\"top\":" + Flat + "}", "bad t"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"1\",\"seq\":1,\"t\":1,\"top\":{\"kind\":\"wash\",\"rgb\":[1,2,3],\"alpha\":1.5}}", "bad top (wash)"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"1\",\"seq\":1,\"t\":1,\"top\":{\"kind\":\"flat\",\"rgb\":[1,2,256]}}", "bad top (flat)"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"1\",\"seq\":1,\"t\":1,\"top\":{\"kind\":\"cols\",\"cols\":[]}}", "bad top (cols)"),
            ("{\"v\":1,\"app\":\"helm\",\"hwnd\":\"1\",\"seq\":1,\"t\":1,\"top\":{\"kind\":\"glass\"}}", "bad top"),
        })
            Assert.Equal(why, TopRowHints.CheckMessage(JsonNode.Parse(json)).Bad);
    }

    [Fact]
    public void Cols_4096_are_taken_and_averaged_down_to_480_TOP_364()
    {
        var cols = Enumerable.Range(0, 4096).Select(i => new[] { i % 256, 0, 255 }).ToList();
        var flat = TopRowHints.ResampleCols(cols);
        Assert.Equal(480 * 3, flat.Length);
        Assert.Equal(255, flat[2]);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, TopRowHints.ResampleCols(new List<int[]> { new[] { 1, 2, 3 }, new[] { 4, 5, 6 } }));
        Assert.Equal(new[] { 2, 4, 6 }, TopRowHints.ResampleCols(new List<int[]> { new[] { 1, 3, 5 }, new[] { 3, 5, 7 } }, 1));
        var tooMany = "{\"kind\":\"cols\",\"cols\":[" + string.Join(",", Enumerable.Repeat("[1,2,3]", 4097)) + "]}";
        Assert.Equal("bad top (cols)", TopRowHints.NormaliseTop(JsonNode.Parse(tooMany)).Bad);
    }

    [Fact]
    public void A_line_over_8_KB_is_counted_and_dropped_and_an_exactly_8_KB_CRLF_line_is_taken_TOP_365()
    {
        _owners["65538"] = QuartzExe;
        using var h = Make();
        var c = h.Connect()!;
        var big = Line("quartz", "65538", 1, Flat).TrimEnd('\n');
        Send(h, c, big + new string(' ', 9000) + "\n");
        Assert.Equal(1, Ignored(h, "line over 8 KB"));
        var exact = big + new string(' ', 8192 - big.Length);
        Assert.Equal(8192, Encoding.UTF8.GetByteCount(exact));
        Send(h, c, exact[..4000]); Send(h, c, exact[4000..] + "\r\n");   // in two chunks, as a pipe may deliver it
        Assert.True(Until(() => Fwds() == 1), "the exactly 8 KB line with CRLF was not taken");
        Send(h, c, Line("quartz", "65538", 2, "{\"kind\":\"flat\",\"rgb\":[1,1,1]}"));
        Assert.True(Until(() => Fwds() == 2), "the line after an over-long one did not work");
        Assert.Equal(1, Ignored(h, "line over 8 KB"));
    }

    [Fact]
    public void Seq_must_increase_per_window_and_a_new_connection_may_start_over_TOP_366()
    {
        _owners["65540"] = QuartzExe;
        using var h = Make();
        var c = h.Connect()!;
        Send(h, c, Line("quartz", "65540", 5, Flat));
        Assert.True(Until(() => Fwds() == 1));
        Send(h, c, Line("quartz", "65540", 5, Flat));
        Send(h, c, Line("quartz", "65540", 4, Flat));
        Assert.Equal(2, Ignored(h, "seq not increasing"));
        h.Disconnect(c);
        Assert.Equal("the app disconnected", Last().Why);
        var c2 = h.Connect()!;
        Send(h, c2, Line("quartz", "65540", 0, Flat));
        Assert.True(Until(() => Fwds() == 3), "a new connection could not start its seq over");
    }

    [Fact]
    public void The_senders_window_is_checked_once_and_only_the_apps_process_is_taken_TOP_367()
    {
        Assert.True(TopRowHints.AppOwns("quartz", QuartzExe, Home));
        Assert.True(TopRowHints.AppOwns("quartz", Home + @"\browser\node_modules\electron\dist\electron.exe", Home));
        Assert.True(TopRowHints.AppOwns("helm", Home.ToUpperInvariant() + @"\HELM-looksync\node_modules\electron\dist\electron.exe", Home));
        Assert.False(TopRowHints.AppOwns("helm", Home + @"\helm-\electron.exe", Home));
        Assert.False(TopRowHints.AppOwns("quartz", Home + @"\helm\electron.exe", Home));
        Assert.False(TopRowHints.AppOwns("quartz", @"C:\Other\browser\electron.exe", Home));
        Assert.False(TopRowHints.AppOwns("helm", QuartzExe, Home));

        _owners["65542"] = QuartzExe; _owners["65544"] = @"C:\Windows\notepad.exe";
        using var h = Make();
        var c = h.Connect()!;
        Send(h, c, Line("quartz", "65542", 1, Flat));
        Assert.True(Until(() => Fwds() == 1));
        Send(h, c, Line("quartz", "65542", 2, Flat));
        Assert.True(Until(() => Fwds() == 2));
        Assert.Equal(1, _checks);   // once per (app, window)
        Send(h, c, Line("helm", "65544", 1, Flat));
        Assert.True(Until(() => Ignored(h, "window is not that app's") == 1), "a window of another program was not refused");
        Send(h, c, Line("helm", "65544", 2, Flat));   // a failed check is not asked again for 30 s
        Assert.Equal(2, Ignored(h, "window is not that app's"));
        Assert.Equal(2, _checks);
        Assert.Equal(2, Fwds());
    }

    [Fact]
    public void Light_theme_keeps_nothing_and_drops_a_kept_window_on_its_next_look_TOP_368()
    {
        _owners["65546"] = QuartzExe;
        using var h = Make();
        var c = h.Connect()!;
        Send(h, c, Line("quartz", "65546", 1, Flat));
        Assert.True(Until(() => Fwds() == 1));
        _dark = false;
        Send(h, c, Line("quartz", "65546", 2, Flat));
        Assert.Equal(2, Fwds());
        Assert.Null(Last().Top);
        Assert.Equal("light theme", Last().Why);
        Assert.Equal(1, Ignored(h, "light theme"));
        _dark = true;
        h.ResendAll();
        Assert.Equal(2, Fwds());   // dark again replays nothing by itself
    }

    [Fact]
    public void None_forgets_the_window_and_a_disconnect_drops_all_its_hints_TOP_369()
    {
        _owners["65548"] = QuartzExe; _owners["65550"] = QuartzExe;
        using var h = Make();
        var c = h.Connect()!;
        Send(h, c, Line("quartz", "65548", 1, Flat) + Line("quartz", "65550", 1, Flat));
        Assert.True(Until(() => Fwds() == 2));
        Send(h, c, Line("quartz", "65548", 2, "{\"kind\":\"none\"}"));
        Assert.Equal(("65548", "quartz", (string?)null, "the app said none"), Last());
        h.ResendAll();
        Assert.Equal("65550", Last().Hwnd);   // only the kept one is sent again (TOP-374)
        h.Disconnect(c);
        Assert.Equal(("65550", "quartz", (string?)null, "the app disconnected"), Last());
        Assert.Equal(0, h.View()["hints"]!["quartz"]!.GetValue<int>());
    }

    [Fact]
    public void At_most_32_clients_and_64_windows_a_client_each_reason_logged_once_a_minute_TOP_370()
    {
        using var h = Make();
        var conns = Enumerable.Range(0, 32).Select(_ => h.Connect()).ToList();
        Assert.True(conns.All(x => x != null), "fewer than 32 clients were taken");
        Assert.Null(h.Connect());
        Assert.Null(h.Connect());
        Assert.Equal(2, Ignored(h, "too many clients"));
        Assert.Single(_logs, l => l.Contains("too many clients", StringComparison.Ordinal));
        Interlocked.Add(ref _now, 60_000);
        Assert.Null(h.Connect());
        Assert.Equal(2, _logs.Count(l => l.Contains("too many clients", StringComparison.Ordinal)));
        Assert.Contains(_logs, l => l == "top-row hints: ignored a message: too many clients (3 so far; logged at most once a minute)");

        var c = conns[0]!;
        for (int i = 1; i <= 65; i++) _owners[(70000 + i).ToString()] = QuartzExe;
        Send(h, c, string.Concat(Enumerable.Range(1, 65).Select(i => Line("quartz", (70000 + i).ToString(), 1, Flat))));
        Assert.True(Until(() => Ignored(h, "too many windows on one client") == 1), "a 65th window on one client was kept");
        Assert.Equal(64, h.View()["hints"]!["quartz"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_real_pipe_busy_says_so_retries_and_then_takes_a_client_TOP_362_371()
    {
        string name = "mantle-hints-test-" + Guid.NewGuid().ToString("N");
        _owners["65552"] = QuartzExe;
        var blocker = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var h = Make(retryMs: 200, pipe: name);
        var r = await h.StartAsync();
        Assert.False(r.Ok);
        Assert.Equal($@"another program (most likely another Mantle) is already listening on \\.\pipe\{name}, so this Mantle cannot take the top-row hints; trying again every 0.2 s", r.Reason);
        blocker.Dispose();
        Assert.True(Until(() => h.View()["listening"]!.GetValue<bool>(), 5000), "it did not listen once the pipe was free");
        Assert.Contains(_logs, l => l.EndsWith("(it was busy before)", StringComparison.Ordinal));

        using (var client = new NamedPipeClientStream(".", name, PipeDirection.Out))
        {
            await client.ConnectAsync(3000);
            var b = Encoding.UTF8.GetBytes(Line("quartz", "65552", 1, Flat));
            await client.WriteAsync(b);
            await client.FlushAsync();
            Assert.True(Until(() => Fwds() == 1), "a hint over the real pipe was not forwarded");
            Assert.Equal(("65552", "quartz", Flat, (string?)null), Last());
        }
        Assert.True(Until(() => Fwds() == 2), "closing the client did not drop its hint");
        Assert.Equal("the app disconnected", Last().Why);
        h.Stop();
        Assert.False(h.View()["listening"]!.GetValue<bool>());
    }

    [Fact]
    public void A_test_run_never_takes_the_live_pipe()
    {
        Assert.Equal(@"\\.\pipe\look-sync-toprow-test-77", TopBarFeature.HintsPipe(true, null, 77).Pipe);
        var (pipe, note) = TopBarFeature.HintsPipe(true, "look-sync-toprow", 77);
        Assert.Equal(@"\\.\pipe\look-sync-toprow-test-77", pipe);
        Assert.NotNull(note);
        Assert.Equal(@"\\.\pipe\look-sync-toprow", TopBarFeature.HintsPipe(false, null, 77).Pipe);
        Assert.Equal(@"\\.\pipe\mine", TopBarFeature.HintsPipe(true, "mine", 77).Pipe);
    }
}
