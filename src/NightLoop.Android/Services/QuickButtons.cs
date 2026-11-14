using Android.Content;
using Android.Graphics;
using Android.Text;
using Android.Views;
using Android.Widget;
using NightLoop.Mobile.Core;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile;

/// <summary>
/// Quick-action buttons: for each macro whose "Quick action" switch is on in the bubble's list, a button stays on
/// screen over the game. Tap it to play the macro's saved steps, tap it again to stop. Drag it anywhere; it comes back
/// where it was left.
/// </summary>
internal sealed class QuickButtons(Context context, IWindowManager windows)
{
    private readonly Dictionary<Guid, (TextView View, WindowManagerLayoutParams Layout)> _shown = [];
    private readonly HashSet<Guid> _starting = [];

    private int Px(float dp) => Palette.Dp(context, dp);

    private void Move(View v, WindowManagerLayoutParams layout) => windows.UpdateViewLayout(v, layout);

    /// <summary>Shows a button for every quick-action macro that still exists, removes the rest, and repaints them.</summary>
    public void Refresh()
    {
        var macros = Night.Session.IsSignedIn ? Night.AndroidMacros.ToDictionary(m => m.Id) : [];
        var wanted = LocalSettings.QuickActions.Where(macros.ContainsKey).ToHashSet();
        foreach (var id in _shown.Keys.Where(id => !wanted.Contains(id)).ToList()) Remove(id);
        // The list isn't loaded yet (just started): keep what is shown until it is.
        foreach (var id in wanted)
        {
            if (!_shown.ContainsKey(id)) Add(id);
            Paint(id, macros[id].Name);
        }
    }

    public void RemoveAll()
    {
        foreach (var id in _shown.Keys.ToList()) Remove(id);
    }

    private void Add(Guid id)
    {
        var view = new TextView(context) { Gravity = GravityFlags.Center };
        view.SetTextColor(Color.White);
        view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        view.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        view.SetSingleLine(true);
        view.Ellipsize = TextUtils.TruncateAt.End;
        view.SetMaxWidth(Px(180));
        view.SetPadding(Px(14), 0, Px(14), 0);
        var metrics = context.Resources!.DisplayMetrics!;
        var (x, y) = LocalSettings.QuickSpot(id) is { } spot
            ? (Math.Clamp(spot.X, 0, Math.Max(0, Math.Max(metrics.WidthPixels, metrics.HeightPixels) - Px(60))),
               Math.Clamp(spot.Y, 0, Math.Max(0, Math.Max(metrics.WidthPixels, metrics.HeightPixels) - Px(48))))
            : (metrics.WidthPixels - Px(150), Px(220) + _shown.Count * Px(56));
        var layout = new WindowManagerLayoutParams(ViewGroup.LayoutParams.WrapContent, Px(44), WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.LayoutInScreen, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = x,
            Y = y,
            // Android 12+ drops a macro's tap that passes through another app's window more than 80% opaque.
            Alpha = 0.8f,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        view.SetOnTouchListener(new Drag(this, id, layout));
        windows.AddView(view, layout);
        // A macro's own tap under this button goes through to the game, and pictures are picked without it.
        Overlays.Track(view, layout, windows);
        _shown[id] = (view, layout);
    }

    private void Remove(Guid id)
    {
        if (!_shown.Remove(id, out var b)) return;
        Overlays.Untrack(b.View);
        if (b.View.IsAttachedToWindow) windows.RemoveView(b.View);
    }

    private void Paint(Guid id, string name)
    {
        if (!_shown.TryGetValue(id, out var b)) return;
        var running = Night.IsRunning(id) || _starting.Contains(id);
        b.View.Text = running ? $"■ {name}" : $"⚡ {name}";
        b.View.ContentDescription = running ? $"Stop {name}" : $"Play {name}";
        b.View.Background = Ui.Rounded(context, running ? Color.Argb(225, 34, 160, 90) : Color.Argb(225, 124, 92, 246), 22, Color.Argb(200, 237, 233, 254));
    }

    private async Task TappedAsync(Guid id)
    {
        if (Night.IsRunning(id))
        {
            Night.Stop(id);
            return;
        }
        if (!_starting.Add(id)) return;
        Refresh();
        try
        {
            // Plays the saved copy (or the draft of a macro never saved), like ▶ in the app.
            if (await Night.PlayAsync(id) is { } error) Night.OnMain(() => Ui.Toast(context, error));
        }
        finally
        {
            Night.OnMain(() =>
            {
                _starting.Remove(id);
                Refresh();
            });
        }
    }

    private sealed class Drag(QuickButtons owner, Guid id, WindowManagerLayoutParams layout) : Java.Lang.Object, View.IOnTouchListener
    {
        private float _downX, _downY;
        private int _startX, _startY;
        private bool _moved;

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (v is null || e is null) return false;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    (_downX, _downY, _startX, _startY, _moved) = (e.RawX, e.RawY, layout.X, layout.Y, false);
                    return true;
                case MotionEventActions.Move:
                    var (dx, dy) = (e.RawX - _downX, e.RawY - _downY);
                    if (Math.Abs(dx) > 12 || Math.Abs(dy) > 12) _moved = true;
                    if (_moved)
                    {
                        layout.X = _startX + (int)dx;
                        layout.Y = _startY + (int)dy;
                        owner.Move(v, layout);
                    }
                    return true;
                case MotionEventActions.Cancel:
                case MotionEventActions.Up:
                    if (_moved) LocalSettings.SaveQuickSpot(id, layout.X, layout.Y);
                    else if (e.Action == MotionEventActions.Up) _ = owner.TappedAsync(id);
                    return true;
            }
            return false;
        }
    }
}
