using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using NightLoop.Mobile.Core;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile;

/// <summary>
/// Keeps the floating Night-loop bubble on screen above other apps, as a foreground service with a "running"
/// notification. Drag the bubble anywhere; tap it to open the macro panel, where your Android macros play and stop.
/// The bubble gets a green ring while a macro is playing.
/// </summary>
[Service(Name = "win.nightloop.app.BubbleService", Exported = false,
         ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class BubbleService : Service
{
    public const string ActionStop = "win.nightloop.app.STOP_BUBBLE";
    private const string Channel = "nightloop_running";
    private const int NotificationId = 1;

    public static bool Running { get; private set; }

    private IWindowManager? _windows;
    private View? _bubble;
    private WindowManagerLayoutParams? _bubbleLayout;
    private LinearLayout? _panel;
    private LinearLayout? _panelList;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        Night.Init(this);
        StartAsForeground();
        if (_bubble is null && Setup.Permissions.CanDrawOverlays(this)) ShowBubble();
        Running = true;
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        Night.RunsChanged -= OnRunsChanged;
        Night.MacrosChanged -= FillPanel;
        _editor?.Dismiss();
        ClosePanel();
        if (_bubble is not null) _windows?.RemoveView(_bubble);
        _bubble = null;
        Running = false;
        base.OnDestroy();
    }

    private void StartAsForeground()
    {
        var manager = (NotificationManager)GetSystemService(NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Night-loop running", NotificationImportance.Low));

        var stop = PendingIntent.GetService(this, 0,
            new Intent(this, typeof(BubbleService)).SetAction(ActionStop), PendingIntentFlags.Immutable);
        var open = PendingIntent.GetActivity(this, 0,
            new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask), PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(this, Channel)
            .SetContentTitle("Night-loop is running")
            .SetContentText("The bubble is on. Tap Stop to hide it.")
            .SetSmallIcon(Resource.Drawable.ic_nightloop)
            .SetContentIntent(open)
            .SetOngoing(true)
            .AddAction(new Notification.Action.Builder((Icon?)null, "Stop", stop).Build())
            .Build();

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }

    private void ShowBubble()
    {
        _windows = GetSystemService(WindowService)!.JavaCast<IWindowManager>();
        var size = (int)(56 * Resources!.DisplayMetrics!.Density);
        var bubble = new ImageView(this);
        bubble.SetImageResource(Resource.Drawable.ic_nightloop);
        bubble.SetScaleType(ImageView.ScaleType.FitCenter);
        bubble.ContentDescription = "Night-loop";

        var layout = new WindowManagerLayoutParams(size, size,
            WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = 24,
            Y = (int)(160 * Resources.DisplayMetrics.Density),
        };
        bubble.SetOnTouchListener(new BubbleDrag(this, layout));
        _windows.AddView(bubble, layout);
        _bubble = bubble;
        _bubbleLayout = layout;
        Night.RunsChanged += OnRunsChanged;
        Night.MacrosChanged += FillPanel;
        OnRunsChanged();
    }

    private void OnRunsChanged()
    {
        if (_bubble is ImageView bubble)
        {
            var pad = Palette.Dp(this, 3);
            bubble.SetPadding(pad, pad, pad, pad);
            bubble.Background = Night.AnyRunning ? Ui.Rounded(this, Color.Transparent, 28, Palette.Good) : null;
            if (Night.AnyRunning && bubble.Background is GradientDrawable ring) ring.SetStroke(Palette.Dp(this, 3), Palette.Good);
        }
        FillPanel();
    }

    // ---- The macro panel ----

    private void TogglePanel()
    {
        if (_panel is not null)
        {
            ClosePanel();
            return;
        }
        var panel = Ui.Column(this, Palette.Dp(this, 12));
        panel.Background = Ui.Rounded(this, Palette.Surface, 14, Palette.Border);
        var top = Ui.Row(this);
        top.AddView(Ui.Fill(Ui.Text(this, "Night-loop", 16, Palette.Text, bold: true)));
        top.AddView(Ui.Small(this, "⌂", OpenApp, "Open Night-loop"));
        top.AddView(Ui.Small(this, "✕", ClosePanel, "Close"));
        panel.AddView(top);
        _panelList = Ui.Column(this, 0);
        var scroll = new ScrollView(this);
        scroll.AddView(_panelList);
        panel.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var metrics = Resources!.DisplayMetrics!;
        var width = Math.Min(Palette.Dp(this, 300), metrics.WidthPixels - Palette.Dp(this, 24));
        var layout = new WindowManagerLayoutParams(width, ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay, WindowManagerFlags.NotFocusable, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Math.Clamp((_bubbleLayout?.X ?? 0), Palette.Dp(this, 12), metrics.WidthPixels - width - Palette.Dp(this, 12)),
            Y = (_bubbleLayout?.Y ?? 0) + Palette.Dp(this, 64),
        };
        _windows!.AddView(panel, layout);
        _panel = panel;
        FillPanel();
        if (Night.Session.IsSignedIn) _ = RefreshAsync();
    }

    private static async Task RefreshAsync()
    {
        try { await Night.RefreshMacrosAsync(); }
        catch (Exception ex) when (ex is Client.ApiException or Client.ServerUnreachableException) { }
    }

    private void FillPanel()
    {
        if (_panelList is null) return;
        _panelList.RemoveAllViews();
        if (!Night.Session.IsSignedIn)
        {
            _panelList.AddView(Ui.Text(this, "Sign in to Night-loop to make and play macros.", 14, Palette.TextQuiet));
            _panelList.AddView(Ui.Button(this, "Open Night-loop", OpenApp));
            return;
        }
        _panelList.AddView(Ui.Text(this, "Tap a macro to show its dots. PC macros only play on PC.", 12, Palette.TextQuiet));
        var macros = Night.AndroidMacros.ToList();
        if (macros.Count == 0)
            _panelList.AddView(Ui.Text(this, "No Android macros yet. Tap + New macro.", 14, Palette.TextQuiet));
        foreach (var macro in macros)
        {
            var row = Ui.Row(this);
            row.SetPadding(0, Palette.Dp(this, 6), 0, 0);
            var running = Night.IsRunning(macro.Id);
            var editing = _editor?.MacroId == macro.Id;
            var name = Ui.Text(this, editing ? $"✥ {macro.Name}" : macro.Name, 15, running ? Palette.Good : editing ? Palette.Accent : Palette.Text,
                bold: running || editing);
            name.SetPadding(0, Palette.Dp(this, 8), 0, Palette.Dp(this, 8));
            var id = macro.Id;
            name.Click += (_, _) => _ = EditAsync(id);
            row.AddView(Ui.Fill(name));
            row.AddView(running
                ? Ui.Small(this, "■", () => Night.Stop(id), $"Stop {macro.Name}")
                : Ui.Small(this, "▶", () => _ = PlayAsync(id), $"Play {macro.Name}"));
            _panelList.AddView(row);
        }
        var actions = Ui.Row(this);
        actions.AddView(Ui.Button(this, "+ New macro", () => _ = NewMacroAsync()));
        if (Night.AnyRunning) actions.AddView(Ui.Button(this, "Stop all", Night.StopAll, primary: false));
        _panelList.AddView(actions);
    }

    // ---- Dot editor ----

    private DotEditor? _editor;
    private bool _opening;

    /// <summary>Selecting a macro puts its numbered dots on the screen (saving the one that was open).</summary>
    private async Task EditAsync(Guid id, bool quiet = false)
    {
        if (_opening) return;
        ClosePanel();
        if (_editor?.MacroId == id) return;
        _opening = true;
        try
        {
            if (_editor is { } open) await open.CloseAsync();
            var (editor, error) = await DotEditor.OpenAsync(this, _windows!, id);
            Night.OnMain(() =>
            {
                if (error is not null)
                {
                    if (!quiet) Ui.Toast(this, error);
                    return;
                }
                _editor = editor;
                editor!.Closed += () =>
                {
                    if (_editor == editor) _editor = null;
                };
            });
        }
        finally
        {
            _opening = false;
        }
    }

    private async Task NewMacroAsync()
    {
        var taken = new HashSet<string>(Night.Macros.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var name = Enumerable.Range(1, 1000).Select(n => $"Phone macro {n}").First(n => !taken.Contains(n));
        try
        {
            var id = await Night.CreateMacroAsync(name);
            await EditAsync(id);
            Night.OnMain(() => Ui.Toast(this, "Tap + to add a dot, then drag it where to tap. ⚙ renames it."));
        }
        catch (Exception ex) when (ex is Client.ApiException or Client.ServerUnreachableException)
        {
            Night.OnMain(() => Ui.Toast(this, ex.Message));
        }
    }

    /// <summary>Plays with the macro's steps on screen (see-through, the playing one lit), or plainly when it has none.</summary>
    private async Task PlayAsync(Guid id)
    {
        ClosePanel(); // out of the way of the taps
        if (_editor?.MacroId != id) await EditAsync(id, quiet: true);
        if (_editor?.MacroId == id)
        {
            await _editor.TogglePlayAsync();
            return;
        }
        var error = await Night.PlayAsync(id);
        if (error is not null) Night.OnMain(() => Ui.Toast(this, error));
    }

    private void ClosePanel()
    {
        if (_panel is not null) _windows?.RemoveView(_panel);
        _panel = null;
        _panelList = null;
    }

    private void OpenApp()
    {
        ClosePanel();
        StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront));
    }

    internal void Move(View view, WindowManagerLayoutParams layout) => _windows?.UpdateViewLayout(view, layout);

    internal void BubbleTapped() => TogglePanel();

    /// <summary>Drags the bubble with the finger; a touch that hardly moves counts as a tap.</summary>
    private sealed class BubbleDrag(BubbleService service, WindowManagerLayoutParams layout) : Java.Lang.Object, View.IOnTouchListener
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
                        service.Move(v, layout);
                    }
                    return true;
                case MotionEventActions.Up:
                    if (!_moved) service.BubbleTapped();
                    return true;
            }
            return false;
        }
    }
}
