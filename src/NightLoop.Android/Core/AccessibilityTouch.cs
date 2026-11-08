using Android.AccessibilityServices;
using Android.Content;
using Path = Android.Graphics.Path;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;

namespace NightLoop.Mobile.Core;

/// <summary>Sends a macro's taps, swipes and button presses through Night-loop's accessibility service.</summary>
internal sealed class AccessibilityTouch : ITouchSender
{
    public static readonly AccessibilityTouch Instance = new();

    private static NightLoopAccessibilityService Service => NightLoopAccessibilityService.Current
        ?? throw new MacroFaultException("Night-loop's accessibility service is off. Turn it on in Night-loop's Set up.");

    public static bool Ready => NightLoopAccessibilityService.Current is not null;

    public string? ForegroundApp => NightLoopAccessibilityService.ForegroundPackage;

    public Task TapAsync(int x, int y, int holdMs, CancellationToken ct)
    {
        var path = new Path();
        path.MoveTo(x, y);
        return DispatchAsync(path, holdMs, ct);
    }

    public Task SwipeAsync(int fromX, int fromY, int toX, int toY, int durationMs, CancellationToken ct)
    {
        var path = new Path();
        path.MoveTo(fromX, fromY);
        path.LineTo(toX, toY);
        return DispatchAsync(path, durationMs, ct);
    }

    public void PressButton(string button)
    {
        var action = button switch
        {
            "home" => GlobalAction.Home,
            "recents" => GlobalAction.Recents,
            "notifications" => GlobalAction.Notifications,
            _ => GlobalAction.Back,
        };
        var service = Service;
        Night.OnMain(() => service.PerformGlobalAction(action));
    }

    public bool OpenApp(string package)
    {
        var service = Service;
        var intent = service.PackageManager?.GetLaunchIntentForPackage(package);
        if (intent is null) return false;
        intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ResetTaskIfNeeded);
        Night.OnMain(() => service.StartActivity(intent));
        return true;
    }

    private static async Task DispatchAsync(Path path, int durationMs, CancellationToken ct)
    {
        var service = Service;
        var duration = Math.Clamp(durationMs, 1, (int)GestureDescription.MaxGestureDuration);
        var gesture = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, duration))!
            .Build()!;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Night.OnMain(() =>
        {
            if (!service.DispatchGesture(gesture, new GestureDone(done), null))
                done.TrySetException(new MacroFaultException("Android didn't accept the touch. Check that Night-loop's accessibility service is on."));
        });
        await using (ct.Register(() => done.TrySetCanceled(ct)))
            await done.Task;
    }

    /// <summary>Completes when Android has played the gesture. A cancelled one (you touched the screen) still counts.</summary>
    private sealed class GestureDone(TaskCompletionSource done) : AccessibilityService.GestureResultCallback
    {
        public override void OnCompleted(GestureDescription? gestureDescription) => done.TrySetResult();
        public override void OnCancelled(GestureDescription? gestureDescription) => done.TrySetResult();
    }
}

/// <summary>Android has no keyboard or mouse blocks; the engine still wants an input sender.</summary>
internal sealed class NoInput : IInputSender
{
    public static readonly NoInput Instance = new();
    public void KeyDown(int virtualKey) { }
    public void KeyUp(int virtualKey) { }
    public void MouseDown(MouseButton button) { }
    public void MouseUp(MouseButton button) { }
    public void MoveTo(int x, int y) { }
    public void MoveBy(int dx, int dy) { }
    public void Scroll(int notches) { }
    public void TypeChar(char c) { }
    public (int X, int Y) CursorPosition => (0, 0);
}
