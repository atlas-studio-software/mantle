using System;
using System.Collections.Generic;
using Mantle.Core.State;

namespace Mantle.Core.Features.Switcher;

/// <summary>The user's two switches (switcher.json): both default on.</summary>
public sealed record SwitcherSettings(bool AltTab, bool WinKeys)
{
    public static readonly SwitcherSettings Default = new(true, true);
}

/// <summary>Can the overlay page draw a session? <c>Failed</c>: it crashed or would not load (a fault), not a first load.</summary>
public sealed record SwitcherPage(bool Ready, string? Reason, bool Failed = false);

/// <summary>The sidecar's answer to <c>hotkey {altTab, winKeys}</c> (its Status()); a field it left out is null.</summary>
public sealed record HookAnswer(bool? Installed, bool? AltTab, bool? WinKeys, bool? Tracking = null, string? Reason = null);

/// <summary>What <see cref="SwitcherHook.StateWords"/> reads: lib/switcher.js computeState()'s object.</summary>
public sealed record SwitcherFacts(
    SwitcherSettings Settings,
    SwitcherPage Page,
    HookAnswer? Hook = null,
    bool Test = false,
    string? Quitting = null,
    bool Live = true,
    string? ReleasedWhy = null,
    string? HookErr = null,
    bool Applying = false,
    bool PendingApply = false,
    string? SettingsNote = null,
    string? Resting = null);

/// <summary>What <c>applyHook</c> does next with the sidecar.</summary>
public enum HookStep
{
    /// <summary>The switcher has not started: nothing.</summary>
    Nothing,
    /// <summary>Test run, or Mantle has not taken the shell (or gave it back): publish the words, ask nothing.</summary>
    PublishOnly,
    /// <summary>A real Alt+Tab is on screen: pulling the hook from under a held Alt would end it unswitched, so wait for it.</summary>
    WaitForSession,
    /// <summary>The sidecar cannot be asked (stopped, restarting): say why, ask nothing.</summary>
    Blocked,
    /// <summary>Send <c>hotkey {altTab, winKeys}</c> (4 s).</summary>
    Ask,
}

/// <summary>
/// The main-process half of lib/switcher.js that decides what the keyboard hook is asked for and what the
/// state piece, the tray and the log say about it (applyHook, stateWords, restWords, trayItems). PURE: the
/// sidecar owns every key and every session (sidecar/AltTab.cs, linked), so a slow or dead Mantle can never
/// hold a key; this only decides when Alt+Tab is Mantle's and when it is handed back to Windows.
/// </summary>
public static class SwitcherHook
{
    public const int AskTimeoutMs = 4000;
    public const int TrayLineMax = 110;
    public const string TrayAltTabLabel = "Alt+Tab opens Mantle's switcher";
    public const string TrayWinKeysLabel = "Win+N / Win+A open Mantle's panels";

    const string Own = "Alt+Tab is Windows' own";
    const string KeysOwn = "Win+N and Win+A open Windows' own panels";

    /// <summary>
    /// The hook request. Alt+Tab goes to Mantle only while the switch is on, the feature is up (on and healthy)
    /// and the page can draw it, and never while Mantle rests: a switcher that switches blind is worse than
    /// Windows' own. Every other case is the HAND-BACK: altTab false, so the sidecar passes every Alt+Tab key.
    /// Win+N / Win+A are independent (the bar's panels stay Mantle's while it rests).
    /// Electron: <c>{ altTab: settings.altTab &amp;&amp; page.ready, winKeys }</c>; rest() and a dead page set
    /// page.ready false there, so the explicit resting and featureUp terms change no answer Electron gives.
    /// </summary>
    public static (bool AltTab, bool WinKeys) Want(SwitcherSettings settings, SwitcherPage page, string? resting, bool featureUp) =>
        (settings.AltTab && featureUp && page.Ready && string.IsNullOrEmpty(resting), settings.WinKeys);

    /// <summary>applyHook's order of checks. <paramref name="sessionOpen"/>: a real (not rehearsal) session is up.</summary>
    public static HookStep Next(bool started, bool test, bool live, bool sessionOpen, string? blocked)
    {
        if (!started) return HookStep.Nothing;
        if (test || !live) return HookStep.PublishOnly;
        if (sessionOpen) return HookStep.WaitForSession;
        if (!string.IsNullOrEmpty(blocked)) return HookStep.Blocked;
        return HookStep.Ask;
    }

    /// <summary>The state's words for a sidecar that cannot be asked.</summary>
    public static string BlockedWords(string blocked) => $"the sidecar cannot be asked ({blocked})";

    /// <summary>The words for a failed ask. An exe from before this feature asks for winTap and nothing else.</summary>
    public static string AskFailedWords(string message) =>
        message.Contains("winTap", StringComparison.Ordinal) && !message.Contains("altTab", StringComparison.Ordinal)
            ? $"the sidecar exe predates Mantle's Alt+Tab (it said: {message}): run npm run build:sidecar"
            : message;

    /// <summary>An ask that came back with no object.</summary>
    public const string EmptyAnswerWords = "the sidecar answered \"hotkey\" with nothing";

    /// <summary>The log says what KIND of rest it is, never the game.</summary>
    public static string RestWords(string? why) =>
        (why ?? "").Contains("game mode", StringComparison.OrdinalIgnoreCase) ? "game mode is on" : "a game runs";

    /// <summary>A tray state line: indented, at most 110 characters (107 + "...").</summary>
    public static string TrayLine(string? s)
    {
        string t = s ?? "";
        return "      " + (t.Length > TrayLineMax ? t.Substring(0, TrayLineMax - 3) + "..." : t);
    }

    /// <summary>
    /// lib/switcher.js stateWords. <c>Broken</c> is for faults only: a switch left ON whose feature is Windows'
    /// own anyway. Not broken: switched off, a test run, before Mantle takes the shell or after it gave it back,
    /// quitting, resting, an ask in flight, a change waiting for the Alt+Tab on screen, a page on its first load.
    /// </summary>
    public static SwitcherPiece StateWords(SwitcherFacts x)
    {
        var h = x.Hook;
        string? hold = null, fault = null;
        if (x.Test) hold = "test mode: a test run never installs a keyboard hook";
        else if (!string.IsNullOrEmpty(x.Quitting)) hold = $"{x.Quitting}; the helper and its keyboard hook were stopped on purpose";
        else if (!x.Live) hold = !string.IsNullOrEmpty(x.ReleasedWhy) ? $"Mantle put Windows back ({x.ReleasedWhy})" : "Mantle has not taken over the shell yet";
        else if (!string.IsNullOrEmpty(x.HookErr)) fault = x.HookErr;
        else if (h is not null && h.Installed == false) fault = !string.IsNullOrEmpty(h.Reason) ? h.Reason : "the sidecar did not install its keyboard hook, and gave no reason";
        string? why = hold ?? fault;
        bool answered = h is not null && !x.Applying;
        string said = h is not null && !string.IsNullOrEmpty(h.Reason) ? $" ({h.Reason})" : "";
        bool altBad = false, keysBad = false;

        bool altTab = false;
        string reason;
        if (!x.Settings.AltTab) reason = $"{Own}: Mantle's switcher is turned off in the tray menu";
        else if (why is not null) { reason = $"{Own}: {why}"; altBad = fault is not null; }
        else if (!string.IsNullOrEmpty(x.Resting)) reason = $"{Own} while Mantle rests ({x.Resting}); Mantle's switcher is back when it exits";
        else if (!x.Page.Ready) { reason = $"{Own}: {x.Page.Reason}, and Mantle will not switch windows blind"; altBad = x.Page.Failed; }
        else if (h is not null && h.AltTab == true && h.Installed == true)
        {
            altTab = true;
            reason = h.Tracking == false
                ? "Alt+Tab opens Mantle's switcher (in Windows' stacking order: the most-recently-used watch did not start)"
                : "Alt+Tab opens Mantle's switcher";
        }
        else if (x.PendingApply) reason = $"{Own} until the Alt+Tab on screen ends";
        else if (answered) { reason = $"{Own}: the sidecar answered without turning Mantle's Alt+Tab on{said}"; altBad = true; }
        else reason = $"{Own}: the sidecar has not confirmed the Alt+Tab hook yet";

        bool winKeys = false;
        string keysReason;
        if (!x.Settings.WinKeys) keysReason = $"{KeysOwn}: turned off in the tray menu";
        else if (why is not null) { keysReason = $"{KeysOwn}: {why}"; keysBad = fault is not null; }
        else if (h is not null && h.WinKeys == true && h.Installed == true) { winKeys = true; keysReason = "Win+N opens Mantle's notifications, Win+A its quick settings"; }
        else if (answered && !x.PendingApply) { keysReason = $"{KeysOwn}: the sidecar answered without turning them on{said}"; keysBad = true; }
        else keysReason = $"{KeysOwn}: the sidecar has not confirmed the hook yet";

        var bad = new List<string>();
        if (altBad) bad.Add(reason);
        // One cause is said once: a stale exe or a dead sidecar breaks both the same way.
        if (keysBad && !(altBad && fault is not null)) bad.Add(keysReason);
        else if (keysBad) bad.Add("Win+N and Win+A too");
        if (!string.IsNullOrEmpty(x.SettingsNote)) reason += "; " + x.SettingsNote;
        return new SwitcherPiece(new SwitcherWant(x.Settings.AltTab, x.Settings.WinKeys), x.Page.Ready ? "ready" : "down",
            altTab, reason, winKeys, keysReason, bad.Count > 0 ? string.Join("; ", bad) : null);
    }
}
