using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile;

/// <summary>
/// Picks spots on the real screen: Night-loop steps aside, a see-through layer covers the screen, and each touch
/// is recorded in screen pixels (the same numbers the tap and swipe blocks use). Then Night-loop comes back.
/// </summary>
internal static class ScreenPicker
{
    /// <summary>Asks for <paramref name="prompts"/>.Length spots, one per touch, and calls back with them on the main thread.</summary>
    public static void Pick(Context activity, string[] prompts, Action<IReadOnlyList<(int X, int Y)>> done)
    {
        var app = activity.ApplicationContext ?? activity;
        if (!Setup.Permissions.CanDrawOverlays(app))
        {
            Ui.Toast(activity, "Turn on \"Display over other apps\" (Set up) to pick spots on the screen.");
            return;
        }
        var windows = app.GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>()!;
        var layer = new FrameLayout(app);
        layer.SetBackgroundColor(Color.Argb(60, 15, 11, 26));
        var hint = Ui.Text(app, prompts[0], 16, Palette.Text, bold: true);
        hint.SetBackgroundColor(Color.Argb(220, 26, 20, 48));
        hint.SetPadding(Palette.Dp(app, 16), Palette.Dp(app, 12), Palette.Dp(app, 16), Palette.Dp(app, 12));
        hint.Gravity = GravityFlags.Center;
        layer.AddView(hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));

        var picked = new List<(int X, int Y)>();
        var flip = false;
        layer.Touch += (_, e) =>
        {
            if (e.Event?.Action != MotionEventActions.Up) return;
            picked.Add(((int)e.Event.RawX, (int)e.Event.RawY));
            if (picked.Count < prompts.Length)
            {
                hint.Text = prompts[picked.Count];
                return;
            }
            windows.RemoveView(layer);
            ComeBack(activity, app);
            try { done(picked); }
            catch (Exception ex) { Ui.Toast(app, $"Couldn't pick that spot: {ex.Message}"); }
        };
        hint.Click += (_, _) =>
        {
            flip = !flip;
            ((FrameLayout.LayoutParams)hint.LayoutParameters!).Gravity = flip ? GravityFlags.Bottom : GravityFlags.Top;
            hint.RequestLayout();
        };

        var layout = new WindowManagerLayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen | WindowManagerFlags.LayoutNoLimits,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        (activity as Android.App.Activity)?.MoveTaskToBack(true);
        windows.AddView(layer, layout);
    }

    private static void ComeBack(Context from, Context app)
    {
        if (from is Android.App.Activity a)
            a.StartActivity(new Intent(app, a.GetType()).AddFlags(ActivityFlags.ReorderToFront | ActivityFlags.NewTask));
    }

    /// <summary>
    /// Drag a box over what to look for. Calls back with the box in screen pixels once the box layer is gone, so the
    /// picture taken afterwards shows only the screen underneath.
    /// </summary>
    public static void PickBox(Context activity, string prompt, Action<(int Left, int Top, int Width, int Height)> done)
    {
        var app = activity.ApplicationContext ?? activity;
        if (!Setup.Permissions.CanDrawOverlays(app))
        {
            Ui.Toast(activity, "Turn on \"Display over other apps\" (Set up) to pick on the screen.");
            return;
        }
        var windows = app.GetSystemService(Context.WindowService)!.JavaCast<IWindowManager>()!;
        var layer = new FrameLayout(app);
        layer.SetBackgroundColor(Color.Argb(40, 15, 11, 26));
        var hint = Ui.Text(app, prompt, 16, Palette.Text, bold: true);
        hint.SetBackgroundColor(Color.Argb(220, 26, 20, 48));
        hint.SetPadding(Palette.Dp(app, 16), Palette.Dp(app, 12), Palette.Dp(app, 16), Palette.Dp(app, 12));
        hint.Gravity = GravityFlags.Center;
        layer.AddView(hint, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top));
        var box = new View(app);
        var outline = new Android.Graphics.Drawables.GradientDrawable();
        outline.SetStroke(Palette.Dp(app, 2), Color.Argb(255, 167, 139, 250));
        outline.SetColor(Color.Argb(30, 167, 139, 250));
        box.Background = outline;
        box.Visibility = ViewStates.Gone;
        layer.AddView(box, new FrameLayout.LayoutParams(0, 0));
        float sx = 0, sy = 0;
        layer.Touch += (_, e) =>
        {
            var ev = e.Event!;
            switch (ev.Action)
            {
                case MotionEventActions.Down:
                    sx = ev.RawX;
                    sy = ev.RawY;
                    box.Visibility = ViewStates.Visible;
                    break;
                case MotionEventActions.Move:
                    var lp = (FrameLayout.LayoutParams)box.LayoutParameters!;
                    lp.LeftMargin = (int)Math.Min(sx, ev.RawX);
                    lp.TopMargin = (int)Math.Min(sy, ev.RawY);
                    lp.Width = (int)Math.Abs(ev.RawX - sx);
                    lp.Height = (int)Math.Abs(ev.RawY - sy);
                    box.RequestLayout();
                    break;
                case MotionEventActions.Up:
                    var l = (int)Math.Min(sx, ev.RawX);
                    var t = (int)Math.Min(sy, ev.RawY);
                    var w = (int)Math.Abs(ev.RawX - sx);
                    var h = (int)Math.Abs(ev.RawY - sy);
                    if (w < 8 || h < 8)
                    {
                        hint.Text = "Drag a bigger box around it.";
                        box.Visibility = ViewStates.Gone;
                        break;
                    }
                    windows.RemoveView(layer);
                    // Give the screen a moment to redraw without the box before the picture is taken.
                    new Android.OS.Handler(Android.OS.Looper.MainLooper!).PostDelayed(() =>
                    {
                        try { done((l, t, w, h)); }
                        catch (Exception ex) { Ui.Toast(app, $"Couldn't take the picture: {ex.Message}"); }
                        ComeBack(activity, app);
                    }, 350);
                    break;
            }
        };
        var layout = new WindowManagerLayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutInScreen | WindowManagerFlags.LayoutNoLimits,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        (activity as Android.App.Activity)?.MoveTaskToBack(true);
        windows.AddView(layer, layout);
    }
}
