using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Commands;
using Mantle.Core.Data;
using Mantle.Core.Platform;

namespace Mantle.Core.Features;

// A feature is the native form of lib/features/<name>.js (lib/features/README.md is the spec): a
// module found at start-up, with no central list file, that the feature host installs, stops, rests
// and restarts without restarting Mantle. The same lifecycle rules: DOWN with a reason, a 10 s install
// deadline, everything it registered released when an install fails, chords taken from Windows only
// while it is on AND healthy AND live, and rest while a game runs. Contract only: native-feature-host
// implements the host and the context; features themselves are ported from round 3 on.
//
// Threading: InstallAsync, StopAsync and every context member run on the host thread, and every
// handler a feature registers is called on the host thread. A feature that needs a UI thread asks its
// surface through a command or an event; one that needs Windows asks the platform.

/// <summary>
/// Marks a class as a feature. Mantle.Generators (native-core) emits a <see cref="FeatureDescriptor"/>
/// for each one; the module fields of lib/features/&lt;name&gt;.js become the attribute's.
/// </summary>
/// <example><c>[Feature("clipboard", "Clipboard history (Win+V)", defaultOn: true, Chords = new[] { "Win+V" }, Rest = true)]</c></example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class FeatureAttribute : Attribute
{
    public FeatureAttribute(string name, string label, bool defaultOn)
    {
        Name = name; Label = label; DefaultOn = defaultOn;
    }

    /// Lower-case letters and digits, the Electron module's file name (so its settings file and commands keep their names).
    public string Name { get; }
    /// The tray checkbox text and the strip's row title.
    public string Label { get; }
    /// The tray switch until the user flips it (then features.json remembers).
    public bool DefaultOn { get; }
    /// Chords taken from Windows only while the feature is on, healthy and live (<c>Win+V</c>, <c>Ctrl+Shift+Esc</c>, ...).
    public string[] Chords { get; set; } = Array.Empty<string>();
    /// Keys reported to the feature and never swallowed (the volume keys).
    public string[] Observe { get; set; } = Array.Empty<string>();
    /// Stopped while a game runs and installed again after.
    public bool Rest { get; set; }
}

/// The option kinds Mantle Settings can draw (lib/features/README.md "Options").
public enum FeatureOptionKind { Bool, Choice, Number, Text, Folder }

public sealed record FeatureChoice(string Value, string Label);

/// <summary>One option a feature declares for Mantle Settings, stored in its own settings file.</summary>
/// <param name="Default">A bool, a string, a double, or null (a folder option's "Windows' default").</param>
public sealed record FeatureOption(string Key, string Label, FeatureOptionKind Kind, object? Default)
{
    public string? Help { get; init; }
    public IReadOnlyList<FeatureChoice> Choices { get; init; } = Array.Empty<FeatureChoice>();
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public string? Unit { get; init; }
    /// The words for a null value (<c>"Windows' Screenshots folder"</c>).
    public string? DefaultLabel { get; init; }
}

/// <summary>Everything the host knows about a feature before it is installed.</summary>
/// <param name="Create">A fresh instance for each install (a restart or a wake after a rest gets a new one, with a new context).</param>
public sealed record FeatureDescriptor(
    string Name,
    string Label,
    bool DefaultOn,
    IReadOnlyList<string> Chords,
    IReadOnlyList<string> Observe,
    bool Rest,
    IReadOnlyList<FeatureOption> Options,
    Func<IFeature> Create)
{
    /// What the generator emits for a <c>[Feature]</c> class (and what a test writes by hand).
    public static FeatureDescriptor Of(FeatureAttribute a, Func<IFeature> create, IReadOnlyList<FeatureOption>? options = null) =>
        new(a.Name, a.Label, a.DefaultOn, a.Chords, a.Observe, a.Rest, options ?? Array.Empty<FeatureOption>(), create);
}

/// <summary>Why a feature is being stopped.</summary>
/// <param name="Reason">Plain words ("Mantle is quitting", "VALORANT runs").</param>
/// <param name="Rest">True for a rest: destroy windows, keep what must outlive the game.</param>
public readonly record struct FeatureStop(string Reason, bool Rest);

/// <summary>A feature: one instance per install.</summary>
public interface IFeature
{
    /// <summary>Take what the feature needs (commands, chords, platform watches). 10 s to settle, else
    /// the feature is DOWN with that reason; a throw is DOWN with the exception's words. Either way
    /// everything it registered through the context is released.</summary>
    ValueTask InstallAsync(IFeatureContext ctx);

    /// <summary>Put Windows back. 2.5 s each, all features at once; a throw or a hang is logged and
    /// abandoned. Whatever it changed in Windows must ALSO be put back by the platform at its next start
    /// if a crash skipped this.</summary>
    ValueTask StopAsync(FeatureStop stop);
}

/// The run's flags, frozen: --test, and live (taking the shell over) vs not.
public readonly record struct FeatureFlags(bool Test, bool Live);

/// <summary>
/// What a feature gets (lib/features/README.md "ctx"). Everything registered through it is released
/// when the feature stops, rests or fails to install; after that the context refuses new registrations.
/// </summary>
public interface IFeatureContext
{
    /// The feature's name.
    string Name { get; }

    FeatureFlags Flags { get; }

    /// One line in the log: <c>&lt;name&gt;: &lt;text&gt;</c> (newlines flattened).
    void Log(string text);

    /// The platform, scoped to this install: calls default to <see cref="PlatformDefaults.FeatureCallTimeout"/>
    /// and never throw, subscriptions end when the feature stops, and calls after the stop are refused.
    IPlatform Platform { get; }

    /// Register <c>&lt;name&gt;.&lt;verb&gt;</c> on the command bus. A taken name throws, naming the owner.
    IDisposable Command(string verb, CommandHandler handler);

    /// <c>handler(chord)</c> for a chord this feature lists in <see cref="FeatureAttribute.Chords"/> (any other throws).
    /// Delivered only while the feature is on, healthy, live and not resting.
    IDisposable OnChord(string chord, Action<string> handler);

    /// <c>handler(key)</c> for a key this feature lists in <see cref="FeatureAttribute.Observe"/> (any other throws).
    IDisposable OnKey(string key, Action<string> handler);

    /// The state piece <c>features.&lt;name&gt;</c>. <c>Health(false, why)</c> hands the chords back to
    /// Windows at once and the strip and tray show why; <c>Health(true)</c> takes them back.
    void Health(bool ok, string? reason = null);

    /// The tray switch. The feature is installed even while off and must honour it (no panel, no Windows changes while off).
    bool Enabled { get; }

    /// <c>handler(on)</c> on each flip of the tray switch.
    IDisposable OnEnabled(Action<bool> handler);

    /// <c>features\&lt;name&gt;.json</c>: the same store for the life of the process, kept across a rest.
    IStore Settings { get; }

    /// <c>handler(value, key)</c> when Mantle Settings changes one of the declared options (<c>"*"</c> = any). Undeclared keys throw.
    IDisposable OnOption(string key, Action<JsonElement, string> handler);

    /// Send an event to every subscriber (Electron's ctx.broadcast). The name must start with <c>&lt;name&gt;.</c>.
    void Broadcast<T>(string ev, T payload);

    /// The current value of one of this feature's events, given at once to each new subscriber (ctx.replay).
    IDisposable Replay<T>(string ev, Func<T> current);

    /// Cancelled when the feature's stop or rest begins, so its pending work can end.
    CancellationToken Stopping { get; }
}
