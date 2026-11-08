using Android.App;
using Android.Content;
using Android.Content.PM;

namespace NightLoop.Mobile.Editor;

/// <summary>Lists the apps on the phone that have a home-screen icon, for Open app, Wait for app and App in front.</summary>
internal static class AppPicker
{
    public static void Pick(Activity activity, Action<string> picked)
    {
        var apps = Installed(activity);
        new AlertDialog.Builder(activity)
            .SetTitle("Pick an app")!
            .SetItems(apps.Select(a => a.Name).ToArray(), (_, e) => picked(apps[e.Which].Package))!
            .Show();
    }

    public static List<(string Name, string Package)> Installed(Context context)
    {
        var pm = context.PackageManager!;
        var launcher = new Intent(Intent.ActionMain).AddCategory(Intent.CategoryLauncher);
#pragma warning disable CS0618, CA1422 // the flags overload is only on Android 13+
        var found = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? pm.QueryIntentActivities(launcher, PackageManager.ResolveInfoFlags.Of(0))
            : pm.QueryIntentActivities(launcher, 0);
#pragma warning restore CS0618, CA1422
        var apps = found
            .Where(r => r.ActivityInfo?.PackageName is { } p && p != context.PackageName)
            .Select(r => (Name: r.LoadLabel(pm)?.ToString() ?? r.ActivityInfo!.PackageName!, Package: r.ActivityInfo!.PackageName!))
            .DistinctBy(a => a.Package)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var (name, package) in apps) BlockKinds.AppNames[package] = name;
        return apps;
    }
}
