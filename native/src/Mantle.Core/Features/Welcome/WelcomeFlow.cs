using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.Welcome;

/// <summary>What the page asks of the host after an input: nothing, send <c>welcome.done {how}</c>, or call <c>shell.restore</c>.</summary>
public enum WelcomeAsk { None, Done, Restore }

/// <summary>The result of one input: what to ask (with <c>How</c> for Done) and where the keyboard goes.</summary>
public readonly record struct WelcomeStep(WelcomeAsk Ask, string? How = null, string? Focus = null)
{
    public static readonly WelcomeStep Nothing = new(WelcomeAsk.None);
}

/// <summary>A step marker: "Step N of 3: &lt;name&gt;", whether it is the current one (aria-current=step).</summary>
public sealed record StepMarker(int Index, string AriaLabel, string Title, bool Current);

/// <summary>The restore button's state on card 3.</summary>
public enum RestoreState { Idle, Busy, Done, Failed }

/// <summary>
/// The welcome page's model (ui/welcome/welcome.js S, go, leave, fail, restore and the keydown handler), with no surface:
/// the card index, the leaving latch, the save error, the restore state and the button / step-marker views
/// (WEL-024, 026, 027, 038, 042..046, 048). Skip and finish ask exactly once; a failed record clears the latch.
/// The page never changes the shell itself: Restore is only ever asked for on the user's own click.
/// </summary>
public sealed class WelcomeFlow
{
    /// Exit fade before the answer is sent (welcome.js OUT_MS): 140 ms, 0 with reduced motion.
    public const int OutMs = 140;
    /// Card fade-in and step slide budgets (welcome.css), and the reduced-motion fade.
    public const int FadeInMaxMs = 220, SlideMaxMs = 200, ReducedFadeMs = 80;

    public static int ExitDelay(bool reducedMotion) => reducedMotion ? 0 : OutMs;

    /// The keyboard's places in Tab order (WEL-046): step markers, Skip, Restore (card 3 only), Back (not on card 1), Next.
    public IReadOnlyList<string> TabOrder()
    {
        var o = new List<string>();
        for (int i = 0; i < WelcomeCards.Count; i++) o.Add("step:" + i);
        o.Add("skip");
        if (Card.Id == "back" && Restore != RestoreState.Done) o.Add("restore");
        if (Index > 0) o.Add("back");
        o.Add("next");
        return o;
    }

    public int Index { get; private set; }
    public bool Leaving { get; private set; }
    public string Error { get; private set; } = "";
    public RestoreState Restore { get; private set; } = RestoreState.Idle;
    public string RestoreWhy { get; private set; } = "";
    /// welcome.state: test shows the "Test run" chip; packaged swaps in the installer's "For good".
    public bool Test { get; private set; }
    public bool Packaged { get; private set; }
    /// The live corners once corners.state answers (a late answer swaps in place); null = the defaults.
    public CornersState? LiveCorners { get; private set; }
    /// How many times this page asked welcome.done (skip/finish must be exactly once per leaving).
    public int DoneAsks { get; private set; }

    public WelcomeCard Card => WelcomeCards.Cards[Index];
    public bool Last => Index == WelcomeCards.Count - 1;
    public string Primary => Last ? WelcomeCards.Finish : WelcomeCards.Next;
    public string PrimaryTitle => Last ? WelcomeCards.FinishTitle : WelcomeCards.NextTitle;
    public bool BackVisible => Index > 0;
    public string Counter => Card.Step;
    public bool ChipVisible => Test;
    public IReadOnlyList<WelcomeWay> Ways => WelcomeCards.WaysFor(Packaged);
    public CornersView Corners => WelcomeCards.Corners(LiveCorners);
    /// The restore line as drawn: the button label (busy or not), or the done status; Failed adds the alert line.
    public string RestoreText => Restore switch
    {
        RestoreState.Busy => WelcomeCards.RestoreBusy,
        RestoreState.Done => WelcomeCards.RestoreDone,
        _ => WelcomeCards.RestoreLabel,
    };
    public bool RestoreEnabled => Restore is RestoreState.Idle or RestoreState.Failed;
    public string? RestoreAlert => Restore == RestoreState.Failed ? WelcomeCards.RestoreFailed + RestoreWhy : null;

    public IReadOnlyList<StepMarker> Steps() => WelcomeCards.Cards.Select((c, i) =>
        new StepMarker(i, "Step " + (i + 1) + " of " + WelcomeCards.Count + ": " + c.Step, c.Step, i == Index)).ToList();

    /// <summary>welcome.state's answer (a failed one changes nothing: the cards need nothing from it).</summary>
    public void State(bool test, bool packaged) { Test = test; Packaged = packaged; }

    /// <summary>corners.state's answer; a failure (null) keeps the defaults.</summary>
    public void CornersAnswer(CornersState? state) { if (state != null) LiveCorners = state; }

    /// <summary>Go to a card (Next, Back, a step marker). Ignored while leaving, out of range or to the same card.</summary>
    public WelcomeStep Go(int i, string from = "next")
    {
        if (Leaving || i < 0 || i >= WelcomeCards.Count || i == Index) return WelcomeStep.Nothing;
        Index = i;
        Error = "";
        return new(WelcomeAsk.None, null, from == "step" ? "step:" + i : "next");
    }

    /// <summary>The primary button: Next on cards 1-2, finish on card 3.</summary>
    public WelcomeStep NextOrFinish() => Last ? Leave("finished") : Go(Index + 1, "next");
    public WelcomeStep GoBack() => Go(Index - 1, "back");
    public WelcomeStep SkipAll() => Leave("skipped");

    /// <summary>Leave with an answer: latches so skip / finish ask exactly once.</summary>
    public WelcomeStep Leave(string how)
    {
        if (Leaving) return WelcomeStep.Nothing;
        Leaving = true;
        DoneAsks++;
        return new(WelcomeAsk.Done, how);
    }

    /// <summary>welcome.done's answer: ok = the host closes the window; not ok = the card stays with the reason.</summary>
    public WelcomeStep DoneAnswer(bool ok, string? reason)
    {
        if (ok) return WelcomeStep.Nothing;
        Leaving = false;
        Error = "That could not be saved: " + (string.IsNullOrEmpty(reason) ? "Mantle gave no reason" : reason) + ". The welcome may show again next time.";
        return new(WelcomeAsk.None, null, "next");
    }

    /// <summary>The restore button (the user's click only): once per click, not while busy or after it worked.</summary>
    public WelcomeStep RestoreClick()
    {
        if (Restore is RestoreState.Busy or RestoreState.Done) return WelcomeStep.Nothing;
        Restore = RestoreState.Busy;
        return new(WelcomeAsk.Restore);
    }

    /// <summary>shell.restore's answer: done removes the button (keyboard to Next); a failure shows the plain reason and
    /// leaves the button to try again (keyboard to it).</summary>
    public WelcomeStep RestoreAnswer(bool ok, string? reason)
    {
        if (Restore != RestoreState.Busy) return WelcomeStep.Nothing;
        if (ok) { Restore = RestoreState.Done; RestoreWhy = ""; return new(WelcomeAsk.None, null, "next"); }
        Restore = RestoreState.Failed;
        RestoreWhy = string.IsNullOrEmpty(reason) ? "no reason came back" : reason!;
        return new(WelcomeAsk.None, null, "restore");
    }

    /// <summary>
    /// The page's keydown (WEL-045): Right / PageDown = next card (never finishes); Left / PageUp = previous; Enter when
    /// the keyboard is not on a button = Next / finish; Esc = Skip. Ignored with Alt / Ctrl / Meta held or while leaving.
    /// </summary>
    public WelcomeStep Key(string key, bool alt = false, bool ctrl = false, bool meta = false, bool onButton = false)
    {
        if (Leaving || alt || ctrl || meta) return WelcomeStep.Nothing;
        switch (key)
        {
            case "Escape": return Leave("skipped");
            case "ArrowRight": case "PageDown": return Last ? WelcomeStep.Nothing : Go(Index + 1, "next");
            case "ArrowLeft": case "PageUp": return Go(Index - 1, "back");
            case "Enter": return onButton ? WelcomeStep.Nothing : NextOrFinish();
            default: return WelcomeStep.Nothing;
        }
    }
}
