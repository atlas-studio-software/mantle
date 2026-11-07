using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Mantle.Core.Commands;

// The command bus (PLAN.md decisions A7 and A8): every action a surface, the tray, a test or a
// WebView2 fallback page asks of the host is a named command, and the names are Electron's own
// (qs.set, bar.pin, features.list, clipboard.list, ...), because the parity rows, the logs, the call
// log, the tests and the ledger all speak those names. Contract only: native-core implements the bus
// and the source generator; nothing here routes or logs.
//
// Threading: CallAsync and Register are host-thread only, and handlers run on the host thread (a
// single-threaded async context, Node's event-loop semantics, so handlers need no locks). A UI thread
// never calls the bus directly: it goes through its surface context (Mantle.App/Shell/ISurface.cs),
// which posts the call to the host and resumes on the UI thread.

/// <summary>
/// Marks a method as the handler of one named command. Mantle.Generators (native-core) finds these at
/// build time and emits the registration, so there is no central list file and two lanes never edit
/// the same file to add commands.
/// </summary>
/// <remarks>
/// The method must match <see cref="CommandHandler"/>: <c>ValueTask&lt;CommandResult&gt; M(CommandCall call)</c>,
/// static or instance. A static one is registered at start-up. An instance one is registered when
/// its owner registers itself (a feature at install, a surface when it is created) and is dropped
/// with it. Until the generator lands, calling <see cref="ICommandBus.Register"/> by hand has
/// exactly the same effect.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class CommandAttribute : Attribute
{
    public CommandAttribute(string name) { Name = name; }

    /// The Electron name, unchanged: <c>area.verb</c> (a feature's commands are <c>&lt;feature&gt;.&lt;verb&gt;</c>).
    public string Name { get; }

    /// Optional: the name of a <c>bool</c> property or field on the same type (static for a static handler,
    /// instance for an instance one). The generated registration registers the command only when it reads
    /// true at that moment, the way main.js adds a command inside <c>if (flags.test) { ... }</c>
    /// (<c>features.fire</c>). A name that is missing or not a bool is a build error (MANTLE003).
    public string? When { get; set; }
}

/// <summary>One call as its handler sees it.</summary>
/// <param name="Name">The command's name.</param>
/// <param name="Args">The arguments: an object (empty when the caller sent none). Detached, safe to keep.</param>
/// <param name="Caller">Who asked, for the call log and for handlers that answer one window: a surface
/// name (<c>bar</c>, <c>launcher</c>), <c>tray</c>, <c>host</c>, <c>test</c>, or <c>page:&lt;name&gt;</c> for a WebView2 page.</param>
/// <param name="Cancel">Cancelled when the caller gives up or Mantle is shutting down.</param>
public readonly record struct CommandCall(string Name, JsonElement Args, string Caller, CancellationToken Cancel);

/// <summary>
/// A command's answer, never thrown. Electron's handlers threw an Error whose message went on screen;
/// here that message is <see cref="Error"/>. A handler that throws anyway is caught by the bus and
/// answered as a failure in plain words ("command failed: ...").
/// </summary>
/// <param name="Result">The result (detached); <see cref="JsonValueKind.Undefined"/> for a command that returns nothing.</param>
/// <param name="Error">Plain words when <paramref name="Ok"/> is false, else null.</param>
public sealed record CommandResult(bool Ok, JsonElement Result, string? Error)
{
    public static CommandResult Success(JsonElement result = default) => new(true, result, null);

    public static CommandResult Fail(string error)
    {
        if (string.IsNullOrWhiteSpace(error)) throw new ArgumentException("a failed command needs its reason in plain words", nameof(error));
        return new(false, default, error);
    }
}

/// The shape every command handler has.
public delegate ValueTask<CommandResult> CommandHandler(CommandCall call);

/// <summary>One registration.</summary>
/// <param name="Name">The command's name; a second registration of a taken name is refused and named, never silently replaced.</param>
/// <param name="Owner">Who registered it (<c>host</c>, a feature's name, a surface's name), for the refusal words and the logs.</param>
/// <param name="Handler">Runs on the host thread.</param>
public sealed record CommandRegistration(string Name, string Owner, CommandHandler Handler);

/// <summary>The host's command bus.</summary>
public interface ICommandBus
{
    /// <summary>
    /// Run a command by name, never throwing. An unknown name answers
    /// <c>Fail("unknown command: &lt;name&gt;")</c>; a stopped or resting feature's command answers with why.
    /// </summary>
    /// <param name="args">The arguments (null = none). Read before the call returns, so the caller may reuse the object.</param>
    ValueTask<CommandResult> CallAsync(string name, JsonObject? args = null, string caller = "host", CancellationToken cancel = default);

    /// Add one command. Disposing the handle removes it (a feature's commands go when it stops).
    /// Throws InvalidOperationException, naming both owners, when the name is already taken.
    IDisposable Register(CommandRegistration command);

    /// Is this name registered right now?
    bool Has(string name);

    /// Every registered name, for <c>features.list</c>, the diagnostic report and the parity check.
    IReadOnlyCollection<string> Names { get; }
}
