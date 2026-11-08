using Android.Views;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Night-loop's own touchable windows on top of other apps (bubble, its panel, the play bar and its cards).
/// When a macro taps or swipes where one of them is, that window lets touches through for that moment, so the
/// tap reaches the app underneath instead of Night-loop.
/// </summary>
internal static class Overlays
{
    internal sealed record Entry(View View, WindowManagerLayoutParams Layout, IWindowManager Windows);

    private static readonly List<Entry> All = [];

    public static void Track(View view, WindowManagerLayoutParams layout, IWindowManager windows)
    {
        lock (All)
        {
            All.RemoveAll(e => e.View == view);
            All.Add(new Entry(view, layout, windows));
        }
    }

    public static void Untrack(View? view)
    {
        if (view is null) return;
        lock (All) All.RemoveAll(e => e.View == view);
    }

    /// <summary>Main thread only. Windows over any of the points stop taking touches; returns them for <see cref="Restore"/>.</summary>
    public static List<Entry> LetThrough(IReadOnlyList<(int X, int Y)> points)
    {
        List<Entry> snapshot;
        lock (All) snapshot = [.. All];
        var changed = new List<Entry>();
        var at = new int[2];
        foreach (var e in snapshot)
        {
            if (!e.View.IsAttachedToWindow || e.View.Visibility != ViewStates.Visible) continue;
            if ((e.Layout.Flags & WindowManagerFlags.NotTouchable) != 0) continue;
            e.View.GetLocationOnScreen(at);
            var covers = points.Any(p => p.X >= at[0] && p.X < at[0] + e.View.Width && p.Y >= at[1] && p.Y < at[1] + e.View.Height);
            if (!covers) continue;
            try
            {
                e.Layout.Flags |= WindowManagerFlags.NotTouchable;
                e.Windows.UpdateViewLayout(e.View, e.Layout);
                changed.Add(e);
            }
            catch (Java.Lang.Exception) { }
        }
        return changed;
    }

    /// <summary>Main thread only. Makes the windows from <see cref="LetThrough"/> touchable again.</summary>
    public static void Restore(List<Entry> changed)
    {
        foreach (var e in changed)
        {
            e.Layout.Flags &= ~WindowManagerFlags.NotTouchable;
            try
            {
                if (e.View.IsAttachedToWindow) e.Windows.UpdateViewLayout(e.View, e.Layout);
            }
            catch (Java.Lang.Exception) { }
        }
    }
}
