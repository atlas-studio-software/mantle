using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Mantle.Core.Platform;
using Mantle.Core.State;

namespace Mantle.Core.Features.Switcher;

/// <summary>
/// lib/switcher.js applyHook against the platform: asks the sidecar for exactly what <see cref="SwitcherHook.Want"/>
/// allows (<c>hotkey {altTab, winKeys}</c>, 4 s), keeps its answer and the failure words, and builds the facts
/// <see cref="SwitcherHook.StateWords"/> reads. It never holds a key: the sidecar's machine owns every session,
/// and a failed or late ask leaves Alt+Tab as the sidecar last had it (Windows' own on a fresh sidecar).
/// Not thread-safe: one caller (the switcher's owner) at a time, as in Electron's single thread.
/// </summary>
public sealed class SwitcherHookClient
{
    readonly IPlatform _platform;
    readonly Action<string> _log;
    int _applying;

    public SwitcherHookClient(IPlatform platform, Action<string>? log = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _log = log ?? (_ => { });
    }

    /// The sidecar's last answer (null before one, or after an empty one).
    public HookAnswer? Hook { get; private set; }

    /// Why the last ask failed, in words (null when it went through).
    public string? HookErr { get; private set; }

    /// A change that waits for the Alt+Tab on screen to end (its "why"), else null.
    public string? PendingApply { get; private set; }

    /// An ask is in flight: "not confirmed yet" is not a fault meanwhile.
    public bool Applying => _applying > 0;

    /// The sidecar's Status() reply as a <see cref="HookAnswer"/>; null when it is not an object.
    public static HookAnswer? Parse(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) return null;
        string? reason = r.TryGetProperty("reason", out var rs) && rs.ValueKind == JsonValueKind.String ? rs.GetString() : null;
        return new HookAnswer(Flag(r, "installed"), Flag(r, "altTab"), Flag(r, "winKeys"), Flag(r, "tracking"), reason);
    }

    static bool? Flag(JsonElement o, string k)
    {
        if (!o.TryGetProperty(k, out var v)) return null;
        if (v.ValueKind == JsonValueKind.True) return true;
        if (v.ValueKind == JsonValueKind.False) return false;
        return null;
    }

    /// The request body: <c>{altTab, winKeys}</c> and nothing else (the feature host sends the chords).
    public static JsonObject Request(bool altTab, bool winKeys) => new() { ["altTab"] = altTab, ["winKeys"] = winKeys };

    /// <summary>
    /// One applyHook. <paramref name="sessionOpen"/>: a real (not rehearsal) Alt+Tab is up, so the change waits for
    /// it (pulling the hook from under a held Alt would end it without a switch); call again when it ends.
    /// </summary>
    public async ValueTask<HookStep> ApplyAsync(string why, SwitcherSettings settings, SwitcherPage page, string? resting,
        bool featureUp, bool started, bool test, bool live, bool sessionOpen, CancellationToken cancel = default)
    {
        var st = _platform.Status;
        string? blocked = st.State == PlatformState.Down ? (string.IsNullOrEmpty(st.Reason) ? "the sidecar is down" : st.Reason) : null;
        var step = SwitcherHook.Next(started, test, live, sessionOpen, blocked);
        switch (step)
        {
            case HookStep.Nothing:
            case HookStep.PublishOnly:
                return step;
            case HookStep.WaitForSession:
                PendingApply = why;
                return step;
        }
        PendingApply = null;
        if (step == HookStep.Blocked)
        {
            HookErr = SwitcherHook.BlockedWords(blocked!);
            return step;
        }
        var (altTab, winKeys) = SwitcherHook.Want(settings, page, resting, featureUp);
        _applying++;
        PlatformReply r;
        try { r = await _platform.CallAsync("hotkey", Request(altTab, winKeys), new TimeSpan(0, 0, 0, 0, SwitcherHook.AskTimeoutMs), cancel); }
        finally { _applying--; }
        if (r.Ok)
        {
            Hook = Parse(r.Result);
            HookErr = Hook is null ? SwitcherHook.EmptyAnswerWords : null;
        }
        else
        {
            HookErr = SwitcherHook.AskFailedWords(r.Error ?? "no reason was given");
        }
        return step;
    }

    /// The facts for <see cref="SwitcherHook.StateWords"/> from what this client knows plus the caller's.
    public SwitcherFacts Facts(SwitcherSettings settings, SwitcherPage page, bool test, bool live, string? quitting = null,
        string? releasedWhy = null, string? settingsNote = null, string? resting = null) =>
        new(settings, page, Hook, test, quitting, live, releasedWhy, HookErr, Applying, PendingApply is not null, settingsNote, resting);

    /// applyHook's log line: <c>why: reason[; winKeysReason]</c>.
    public void LogState(string why, SwitcherPiece st) =>
        _log($"{why}: {st.Reason}{(string.IsNullOrEmpty(st.WinKeysReason) ? "" : "; " + st.WinKeysReason)}");
}
