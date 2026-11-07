using Android.Content;

namespace NightLoop.Mobile;

internal static class AppInfo
{
    /// <summary>versionName from the project file (ApplicationDisplayVersion), e.g. "0.1.0".</summary>
    public static string Version { get; private set; } = "0.0.0";

    public static void Load(Context context) =>
        Version = context.PackageManager?.GetPackageInfo(context.PackageName!, 0)?.VersionName ?? Version;

    /// <summary>A random id made once per install, sent as X-Install-Id like the PC app's install.id.</summary>
    public static Guid InstallId(Context context)
    {
        var prefs = context.GetSharedPreferences("nightloop", FileCreationMode.Private)!;
        if (Guid.TryParse(prefs.GetString("install_id", null), out var id)) return id;
        id = Guid.NewGuid();
        prefs.Edit()!.PutString("install_id", id.ToString())!.Apply();
        return id;
    }
}
