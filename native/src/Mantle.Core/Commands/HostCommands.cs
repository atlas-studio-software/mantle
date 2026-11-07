using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Mantle.Core.Features;
using Mantle.Core.State;

namespace Mantle.Core.Commands;

/// <summary>
/// The host's own commands, with Electron's names (main.js COMMANDS): <c>state.get</c>, <c>shell.restore</c>,
/// <c>shell.quit</c>, <c>shell.reconnect</c>. Marked [Command]; Mantle.Generators emits
/// <c>bus.RegisterCommands(hostCommands)</c>, so there is no hand list. The roads they take are the app's
/// (the ONE shutdown, the verified restore), handed in as delegates. Host thread.
/// </summary>
public sealed class HostCommands
{
    public const string Owner = "host";
    readonly IShellStateHub _state;
    readonly Func<string, Task> _restore;
    readonly Action<string> _quit;
    readonly Func<string> _reconnect;

    public HostCommands(IShellStateHub state, Func<string, Task> restore, Action<string> quit, Func<string> reconnect)
    {
        _state = state; _restore = restore; _quit = quit; _reconnect = reconnect;
    }

    static string Via(CommandCall c, string what) => $"{what} ({c.Caller})";

    [Command("state.get")]
    public CommandResult StateGet(CommandCall call)
    {
        var s = _state.Current;
        var o = new JsonObject {
            ["mode"] = s.Mode.ToString().ToLowerInvariant(),
            ["problems"] = Problems(s),
        };
        using var d = JsonDocument.Parse(o.ToJsonString());
        return CommandResult.Success(d.RootElement.Clone());
    }

    static JsonArray Problems(ShellState s)
    {
        var a = new JsonArray();
        foreach (var p in ShellStateHub.Problems(s)) a.Add(new JsonObject { ["piece"] = p.Piece, ["reason"] = p.Reason });
        return a;
    }

    [Command("shell.restore")]
    public async ValueTask<CommandResult> Restore(CommandCall call)
    {
        await _restore(Via(call, "Put Windows back was asked")).ConfigureAwait(true);
        return CommandResult.Success();
    }

    [Command("shell.quit")]
    public CommandResult Quit(CommandCall call)
    {
        _quit(Via(call, "Mantle was asked to quit"));
        return CommandResult.Success();
    }

    [Command("shell.reconnect")]
    public CommandResult Reconnect(CommandCall call)
    {
        string said = _reconnect();
        using var d = JsonDocument.Parse(new JsonObject { ["said"] = said }.ToJsonString());
        return CommandResult.Success(d.RootElement.Clone());
    }
}

/// <summary>The generated catalogs of Mantle.Core, reachable from Mantle.App (they are internal to this assembly).</summary>
public static class Catalogs
{
    /// Every [Feature] of Mantle.Core.
    public static IReadOnlyList<FeatureDescriptor> Features => Mantle.Generated.FeatureCatalog.All;

    /// Every [Command] of Mantle.Core: name, owner type, static.
    public static IReadOnlyList<(string Name, string Owner, bool Static)> Commands => Mantle.Generated.CommandCatalog.All;

    /// Register every static [Command] of Mantle.Core.
    public static IDisposable RegisterStatic(ICommandBus bus) => Mantle.Generated.CommandCatalog.RegisterStatic(bus);

    /// Register the host's commands (generated).
    public static IDisposable Register(ICommandBus bus, HostCommands host) => Mantle.Generated.CommandCatalog.RegisterCommands(bus, host);

    /// Register features.list / features.fire (generated from FeatureCommands' [Command] marks; replaces its hand Register).
    public static IDisposable Register(ICommandBus bus, FeatureCommands features) => Mantle.Generated.CommandCatalog.RegisterCommands(bus, features);
}
