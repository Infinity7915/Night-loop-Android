using Android.AccessibilityServices;
using Android.App;
using Android.Views.Accessibility;

namespace NightLoop.Mobile;

/// <summary>
/// The service Android lets perform taps and swipes on other apps. Macros send their gestures and Back/Home
/// presses through it (Core/AccessibilityTouch), and it remembers which app is in front for "Wait for app".
/// </summary>
[Service(Name = JavaName, Label = "Night-loop", Exported = true,
         Permission = "android.permission.BIND_ACCESSIBILITY_SERVICE")]
[IntentFilter(["android.accessibilityservice.AccessibilityService"])]
[MetaData("android.accessibilityservice", Resource = "@xml/accessibility_service")]
public sealed class NightLoopAccessibilityService : AccessibilityService
{
    public const string JavaName = "win.nightloop.app.NightLoopAccessibilityService";

    /// <summary>The running service, or null when it is switched off.</summary>
    public static NightLoopAccessibilityService? Current { get; private set; }

    /// <summary>Package name of the app in front, for "Wait for app" later.</summary>
    public static string? ForegroundPackage { get; private set; }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Current = this;
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e?.EventType != EventTypes.WindowStateChanged || e.PackageName is not { } package) return;
        // Night-loop's own bubble, panel and pickers aren't "the app in front"; its screens are.
        if (package == PackageName && e.ClassName?.StartsWith("win.nightloop.app.", StringComparison.Ordinal) != true) return;
        ForegroundPackage = package;
    }

    public override void OnInterrupt() { }

    public override bool OnUnbind(global::Android.Content.Intent? intent)
    {
        Current = null;
        return base.OnUnbind(intent);
    }

    public override void OnDestroy()
    {
        Current = null;
        base.OnDestroy();
    }
}
