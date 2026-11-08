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
        return DispatchAsync(path, holdMs, ct, [(x, y)]);
    }

    public Task SwipeAsync(int fromX, int fromY, int toX, int toY, int durationMs, CancellationToken ct)
    {
        var path = new Path();
        path.MoveTo(fromX, fromY);
        path.LineTo(toX, toY);
        return DispatchAsync(path, durationMs, ct, [(fromX, fromY), (toX, toY)]);
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

    private static async Task DispatchAsync(Path path, int durationMs, CancellationToken ct, (int X, int Y)[] points)
    {
        // The play bar or the bubble may sit over the spot: let this gesture pass through them.
        var covered = await OnMainAsync(() => Overlays.LetThrough(points));
        if (covered.Count == 0)
        {
            await SendAsync(path, durationMs, ct);
            return;
        }
        try
        {
            await Task.Delay(80, ct); // let Android apply the window change before the touch
            await SendAsync(path, durationMs, ct);
        }
        finally
        {
            Night.OnMain(() => Overlays.Restore(covered));
        }
    }

    private static Task<T> OnMainAsync<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Night.OnMain(() =>
        {
            try { done.TrySetResult(work()); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        return done.Task;
    }

    /// <summary>Taps sent back to back with no gap can land together (games read them as one burst).</summary>
    private const int MinGapMs = 40;
    private static long _lastEnd;

    private static async Task SendAsync(Path path, int durationMs, CancellationToken ct)
    {
        var since = Environment.TickCount64 - Interlocked.Read(ref _lastEnd);
        if (since < MinGapMs) await Task.Delay((int)(MinGapMs - since), ct);
        try { await SendOnceAsync(path, durationMs, ct); }
        finally { Interlocked.Exchange(ref _lastEnd, Environment.TickCount64); }
    }

    private static async Task SendOnceAsync(Path path, int durationMs, CancellationToken ct)
    {
        var duration = Math.Clamp(durationMs, 1, (int)GestureDescription.MaxGestureDuration);
        var gesture = new GestureDescription.Builder()
            .AddStroke(new GestureDescription.StrokeDescription(path, 0, duration))!
            .Build()!;
        // Android can briefly drop the service (it reconnects on its own) or refuse a gesture while another app
        // is switching in. Try a few times before stopping the macro.
        for (var attempt = 0; ; attempt++)
        {
            var service = NightLoopAccessibilityService.Current;
            if (service is not null)
            {
                var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Night.OnMain(() =>
                {
                    try
                    {
                        if (!service.DispatchGesture(gesture, new GestureDone(done), null)) done.TrySetResult(false);
                    }
                    catch (Exception) { done.TrySetResult(false); }
                });
                bool accepted;
                await using (ct.Register(() => done.TrySetCanceled(ct)))
                    accepted = await done.Task;
                if (accepted) return;
            }
            if (attempt >= 5)
                throw new MacroFaultException(service is null
                    ? "Android switched off Night-loop's accessibility service. Turn it on again in Set up."
                    : "Android didn't accept the tap. Some emulators don't support taps from apps; try a real phone.");
            await Task.Delay(200, ct);
        }
    }

    /// <summary>Completes when Android has played the gesture. A cancelled one (you touched the screen) still counts.</summary>
    private sealed class GestureDone(TaskCompletionSource<bool> done) : AccessibilityService.GestureResultCallback
    {
        public override void OnCompleted(GestureDescription? gestureDescription) => done.TrySetResult(true);
        public override void OnCancelled(GestureDescription? gestureDescription) => done.TrySetResult(true);
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
