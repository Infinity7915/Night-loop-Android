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

    /// <summary>How many times an app's window came up (the same app again counts too).</summary>
    public static int AppWindows => Volatile.Read(ref s_appWindows);
    private static int s_appWindows;

    /// <summary>The see-through screen that asks for screen capture ("Start now").</summary>
    private const string ConsentScreen = "win.nightloop.app.ScreenConsentActivity";

    /// <summary>
    /// Open app launched an app and no app's window came up: it was already in front. Android reports nothing then,
    /// so without this "Wait for app" would wait for a window that never comes.
    /// </summary>
    public static void AlreadyInFront(string package, int appWindowsBefore)
    {
        if (AppWindows == appWindowsBefore) ForegroundPackage = package;
    }

    protected override void OnServiceConnected()
    {
        base.OnServiceConnected();
        Current = this;
    }

    public override void OnAccessibilityEvent(AccessibilityEvent? e)
    {
        if (e?.EventType != EventTypes.WindowStateChanged || e.PackageName is not { } package) return;
        // Night-loop's own bubble, panel and pickers aren't "the app in front"; its screens are. The see-through
        // "Start now" screen isn't either: the game stays in front under it and doesn't report again when it closes,
        // so after Play asked for screen capture, "If Roblox is not open" was true while Roblox was open.
        if (package == PackageName && (e.ClassName?.StartsWith("win.nightloop.app.", StringComparison.Ordinal) != true || e.ClassName == ConsentScreen))
            return;
        // The keyboard and the status bar / notification shade report as windows too, but the app stays in front
        // under them. Counting them made "If Roblox is not open" true right after typing, and Open app then waited
        // for Roblox to come to the front while it already was.
        if (IsOverlay(package, e.ClassName)) return;
        ForegroundPackage = package;
        Interlocked.Increment(ref s_appWindows);
    }

    private bool IsOverlay(string package, string? className)
    {
        if (package is "com.android.systemui" or "android") return true;
        if (className is not null && (className.Contains("SoftInputWindow", StringComparison.Ordinal) || className.Contains("InputMethod", StringComparison.Ordinal)))
            return true;
        try
        {
            var ime = Android.Provider.Settings.Secure.GetString(ContentResolver, Android.Provider.Settings.Secure.DefaultInputMethod);
            return ime is { Length: > 0 } && ime.Split('/')[0] == package;
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
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
