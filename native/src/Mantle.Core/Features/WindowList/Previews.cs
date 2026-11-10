using System;
using System.Collections.Generic;
using System.Linq;

namespace Mantle.Core.Features.WindowList;

/// <summary>
/// The pure half of the bar's window previews (ui/bar/bar.js THE WINDOW FLYOUT, lib/thumbs.js): how many cards fit
/// and at what size, where a live picture goes inside its box, and every DWM failure in words. The DWM calls and the
/// cards themselves are in Mantle.App (Surfaces/Bar/Tasks/Thumbs.cs, BarWindow.Previews.cs).
/// </summary>
public static class Previews
{
    /// bar.js FLY_DWELL_MS / FLY_LEAVE_MS: Windows' own hover delay; long enough to cross the 7 px gap.
    public const int DwellMs = 400, LeaveMs = 350;
    /// bar.js SHOT_W x SHOT_H (the box at full size), SHOT_MIN_W (below it a picture says nothing), CARD_PAD, CARD_GAP.
    public const double ShotW = 200, ShotH = 112, ShotMinW = 128, CardPad = 6, CardGap = 6;
    /// bar.js flyBuild: the bar window's width less the pop-up's 8 px inset each side, its padding and border (2 x 9).
    public const double Chrome = 16 + 2 * 9;
    /// bar.js flyClose: a window asked to close and still here after this says "Still open".
    public const int StillOpenMs = 4000;

    public readonly record struct Fit(bool Cards, double ShotW, double ShotH, string? Why);

    /// bar.js flyBuild: as many full-size cards as fit across the bar window; past that they shrink in 8 px steps, and
    /// past the smallest useful size the flyout is the list of titles, with the reason on top.
    public static Fit FitCards(int n, double barWindowWidthDip)
    {
        double avail = barWindowWidthDip - Chrome, sw = ShotW;
        double Need(double w) => n * (w + 2 * CardPad) + (n - 1) * CardGap;
        while (sw > ShotMinW && Need(sw) > avail) sw -= 8;
        if (Need(sw) > avail) return new Fit(false, 0, 0, n + " windows are too many to preview side by side in this bar, so this is the list");
        return new Fit(true, sw, Math.Round(sw * ShotH / ShotW), null);
    }

    /// bar.css .card: padding 6, the 24 px head, 5 px gaps, the box, the 3 px accent line pulled up 1 px.
    public static double CardHeight(double shotH) => 2 * CardPad + 24 + 5 + shotH + 5 + 3 - 1;
    public static double CardWidth(double shotW) => shotW + 2 * CardPad;

    /// The pop-up around a row of n cards: .pop.fly padding 7 + the 1 px border, each side.
    public static (double W, double H) FlyoutSize(int n, double shotW, double shotH)
        => (n * CardWidth(shotW) + (n - 1) * CardGap + 2 * 8, CardHeight(shotH) + 2 * 8);

    /// lib/thumbs.js#toPhysical: the largest rectangle with the SOURCE's aspect ratio that fits inside the box,
    /// centred (letterboxed like Windows' own previews: never squashed). All physical px, client-area relative.
    public static (int Left, int Top, int Right, int Bottom) Letterbox(double bx, double by, double bw, double bh, int srcW, int srcH)
    {
        double w = bw, h = bh;
        if (srcW > 0 && srcH > 0) {
            double s = Math.Min(bw / srcW, bh / srcH);
            w = srcW * s; h = srcH * s;
        }
        int left = (int)Math.Round(bx + (bw - w) / 2), top = (int)Math.Round(by + (bh - h) / 2);
        return (left, top, left + (int)Math.Round(w), top + (int)Math.Round(h));
    }

    /// lib/thumbs.js CLOAK_WHO: who hid a cloaked window (DWMWA_CLOAKED).
    public static string CloakWho(uint v) => v switch {
        1 => "the app itself has hidden it (it is cloaked)",
        2 => "Windows is hiding it: it is on another virtual desktop, or it is a suspended Store app",
        4 => "Windows is hiding it along with the window that owns it",
        _ => $"Windows reports it cloaked ({v})",
    };

    /// lib/thumbs.js#hrWords: the HRESULTs DWM answers here, in words; anything else as its number.
    public static string HrWords(int hr) => unchecked((uint)hr) switch {
        0x80070057 => "DWM said the arguments are invalid (E_INVALIDARG): the window may have closed, or it is not a top-level window",
        0x80070006 => "DWM said the window handle is invalid (E_HANDLE): the window has closed",
        0x80263001 => "desktop composition is off (DWM_E_COMPOSITIONDISABLED), so Windows draws no previews at all",
        0x80070005 => "DWM refused access (E_ACCESSDENIED): the window belongs to a process Mantle is not allowed to preview",
        var u => $"DWM refused (HRESULT 0x{u:X8})",
    };

    /// bar.js winSig: the flyout's windows, in order, as one string.
    public static string Sig(IEnumerable<TaskWindow> wins) => string.Join(",", wins.Select(w => w.Hwnd));

    public enum Refresh { Keep, Update, Rebuild, Close }

    /// bar.js flyRefresh: a push with the same windows updates titles and marks in place; different windows rebuild
    /// the flyout where it stands; no windows left (or the app gone) closes it.
    public static Refresh OnPush(string openSig, TaskItem? now)
    {
        if (now == null || now.Windows.Count == 0) return Refresh.Close;
        return Sig(now.Windows) == openSig ? Refresh.Update : Refresh.Rebuild;
    }

    /// lib/thumbs.js#prune: the hwnds shown that are in no app's windows any more (the card says "this window has closed").
    public static List<string> Gone(IEnumerable<string> shown, IEnumerable<TaskItem> items)
    {
        var alive = new HashSet<string>(items.SelectMany(i => i.Windows).Select(w => w.Hwnd), StringComparer.Ordinal);
        return shown.Where(h => !alive.Contains(h)).ToList();
    }

    /// bar.js flyCard: the title a card shows ("(untitled window)" if none) and its prefix: "Minimized · ", or
    /// "Still open · " for a window asked to close 4 s ago that is still here.
    public static string CardTitle(TaskWindow w) => w.Title.Length > 0 ? w.Title : "(untitled window)";
    public static string CardPrefix(TaskWindow w, bool stillOpen) => stillOpen ? "Still open ·  " : w.Minimized ? "Minimized ·  " : "";
}
