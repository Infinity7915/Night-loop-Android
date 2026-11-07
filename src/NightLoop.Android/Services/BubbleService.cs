using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;

namespace NightLoop.Mobile;

/// <summary>
/// Keeps the floating Night-loop bubble on screen above other apps, as a foreground service with a "running"
/// notification. Drag the bubble anywhere; tap it to open Night-loop. In phase 2 a tap opens the macro panel.
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

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStop)
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        StartAsForeground();
        if (_bubble is null && Setup.Permissions.CanDrawOverlays(this)) ShowBubble();
        Running = true;
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
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
    }

    internal void Move(View view, WindowManagerLayoutParams layout) => _windows?.UpdateViewLayout(view, layout);

    internal void BubbleTapped() =>
        StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask | ActivityFlags.ReorderToFront));

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
