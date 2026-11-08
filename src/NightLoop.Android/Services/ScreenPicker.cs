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
    public static void Pick(Android.App.Activity activity, string[] prompts, Action<IReadOnlyList<(int X, int Y)>> done)
    {
        var app = activity.ApplicationContext!;
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
            activity.StartActivity(new Intent(app, activity.GetType()).AddFlags(ActivityFlags.ReorderToFront | ActivityFlags.NewTask));
            done(picked);
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
        activity.MoveTaskToBack(true);
        windows.AddView(layer, layout);
    }
}
