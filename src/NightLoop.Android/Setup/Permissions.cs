using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;

namespace NightLoop.Mobile.Setup;

/// <summary>One thing the user switches on in Android settings before Night-loop can play macros.</summary>
public sealed record SetupStep(string Title, string Why, bool Done, Action<Activity> Open);

/// <summary>
/// The permissions Night-loop needs and where in Android settings each one is turned on. The setup screen lists
/// them; the bubble only appears once "Display over other apps" is on.
/// </summary>
public static class Permissions
{
    public const int NotificationsRequest = 1001;

    public static bool CanDrawOverlays(Context context) => Settings.CanDrawOverlays(context);

    /// <summary>True when the user has switched on the Night-loop accessibility service in Android settings.</summary>
    public static bool AccessibilityOn(Context context)
    {
        var enabled = Settings.Secure.GetString(context.ContentResolver, Settings.Secure.EnabledAccessibilityServices) ?? "";
        var full = $"{context.PackageName}/{NightLoopAccessibilityService.JavaName}";
        var shortName = $"{context.PackageName}/.{nameof(NightLoopAccessibilityService)}";
        return enabled.Split(':').Any(s => s.Equals(full, StringComparison.OrdinalIgnoreCase)
                                        || s.Equals(shortName, StringComparison.OrdinalIgnoreCase));
    }

    public static bool NotificationsOn(Context context) =>
        !OperatingSystem.IsAndroidVersionAtLeast(33)
        || context.CheckSelfPermission(Manifest.Permission.PostNotifications) == Permission.Granted;

    public static IReadOnlyList<SetupStep> Steps(Context context)
    {
        var restricted = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? " If Android says the setting is restricted, open Night-loop's App info, tap the ⋮ menu, choose \"Allow restricted settings\", then try again."
            : "";
        return
        [
            new("Display over other apps",
                "Shows the floating Night-loop bubble on top of other apps and the home screen.",
                CanDrawOverlays(context),
                a => a.StartActivity(new Intent(Settings.ActionManageOverlayPermission,
                    global::Android.Net.Uri.Parse("package:" + a.PackageName)))),
            new("Night-loop accessibility service",
                "Lets macros tap, swipe and press Back or Home for you. Night-loop only acts while a macro you started is playing." + restricted,
                AccessibilityOn(context),
                a => a.StartActivity(new Intent(Settings.ActionAccessibilitySettings))),
            new("Notifications",
                "Shows \"Night-loop is running\" with a Stop button while the bubble or a macro is on.",
                NotificationsOn(context),
                a =>
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(33))
                        a.RequestPermissions([Manifest.Permission.PostNotifications], NotificationsRequest);
                }),
        ];
    }
}
