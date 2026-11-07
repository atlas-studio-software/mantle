using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Mantle.Core.Features;

// The parts of Electron's ctx that the frozen IFeatureContext does not carry (contracts.md: a new
// need goes in a new interface in the owner's folder). Every context the host makes implements it,
// so a feature that needs one of these casts: `if (ctx is IFeatureHostAccess more) more.OnRest(...)`.
// The host services of ctx.host (openLauncher, barRect, display, bar, media, toasts, banners, updates,
// switcher, theme, desktop strip, wall, look guard) are NOT here: they belong to the surfaces that
// own them and arrive with those lanes (PARITY FEAT-013).

/// <summary>A game rest starting (On, with why) or ending (On false), as ctx.onRest hears it.</summary>
public readonly record struct RestSignal(bool On, string? Why);

/// <summary>What a chord or key delivery did (lib/features.js routeChord / runChord / routeKey).</summary>
/// <param name="Delivered">A handler ran.</param>
/// <param name="Feature">The feature that got it.</param>
/// <param name="Reason">Why it was not delivered, in words.</param>
/// <param name="Handlers">How many handlers ran (observed keys).</param>
public sealed record KeyDelivery(bool Delivered, string? Feature, string? Reason, int Handlers = 0, bool DryRun = false);

/// <summary>One feature as features.list and Mantle Settings see it.</summary>
public sealed record FeatureInfo(
    string Name, string Label, IReadOnlyList<string> Chords, IReadOnlyList<string> Observe,
    bool Enabled, bool Ok, string Reason, bool Running, bool Stopped, string? Resting,
    IReadOnlyList<string> Commands, bool Loaded, bool Keys, string KeysReason, string? Broken,
    IReadOnlyList<OptionView> Options);

/// <summary>One option with its current value, as Mantle Settings draws it (lib/features.js optionView).</summary>
public sealed record OptionView(string Key, string Label, string Help, string Type, JsonElement Default, JsonElement Value, bool IsDefault, string? Note, FeatureOption Option);

/// <summary>The answer to a switch, a restart or an option write: never thrown, always with words.</summary>
public sealed record FeatureAnswer(bool Ok, string? Reason, FeatureInfo? Feature = null, OptionView? Option = null, int Told = 0);

/// <summary>Mantle Settings' road into every feature (ctx.host.features).</summary>
public interface IFeatureDirectory
{
    IReadOnlyList<FeatureInfo> List();
    FeatureAnswer SetEnabled(string name, bool on);
    ValueTask<FeatureAnswer> SetOptionAsync(string name, string key, JsonElement value);
    ValueTask<FeatureAnswer> RestartAsync(string name);
    /// handler(what, feature) after a switch, an option write or a health change; dropped at stop.
    IDisposable OnChange(Action<string, string> handler);
}

/// <summary>The rest of Electron's ctx.</summary>
public interface IFeatureHostAccess
{
    /// handler(signal) when Mantle starts resting for a game and when it wakes, for a feature that stays up.
    IDisposable OnRest(Action<RestSignal> handler);

    /// Hand this feature's chords back to Windows for now while staying healthy (why), or take them again (null).
    void KeysAway(string? why);

    /// Run the feature that owns <paramref name="chord"/> as if it was pressed (a hot corner opening Task view). Never throws.
    KeyDelivery RunChord(string chord, string via, bool dryRun = false);

    /// Is a command by this full name there right now?
    bool HasCommand(string name);

    /// Mantle Settings' view of every feature.
    IFeatureDirectory Features { get; }

    /// A --test run only (null live): fire a chord or an observed key as if the hook had seen it.
    IFeatureTestKeys? Test { get; }
}

/// <summary>ctx.test: test-mode key simulation.</summary>
public interface IFeatureTestKeys
{
    KeyDelivery FireChord(string chord);
    KeyDelivery FireKey(string key);
}
