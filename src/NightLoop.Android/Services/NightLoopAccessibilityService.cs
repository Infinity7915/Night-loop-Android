using Android.AccessibilityServices;
using Android.App;
using Android.Views.Accessibility;

namespace NightLoop.Mobile;

/// <summary>
/// The service Android lets perform taps and swipes on other apps. Phase 1 only connects it, so the setup screen
/// can show it as on, and remembers which app is in front; macros start sending gestures through it in phase 2.
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
        if (e?.EventType == EventTypes.WindowStateChanged && e.PackageName is { } package)
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
