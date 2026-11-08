using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Android.App;
using Android.Content;
using Android.Content.PM;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Auto-update, like the PC app: looks at Night-loop for Android's GitHub releases, downloads a newer .apk and
/// hands it to Android's installer (you confirm once). Checked when Night-loop opens and from "Check for updates".
/// </summary>
internal static class Updater
{
    public const string ReleasesPage = "https://github.com/Infinity7915/Night-loop-Android/releases";
    private const string ReleasesApi = "https://api.github.com/repos/Infinity7915/Night-loop-Android/releases?per_page=10";

    public sealed record Update(Version Version, string ApkUrl, long Size);

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] List<GitHubAsset> Assets);

    private sealed record GitHubAsset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("size")] long Size);

    private static readonly HttpClient Http = MakeHttp();

    private static HttpClient MakeHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Night-loop-Android");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static Version Current => Version.TryParse(AppInfo.Version, out var v) ? v : new Version(0, 0, 0);

    /// <summary>The newest release above this version, or null when up to date.</summary>
    public static async Task<Update?> CheckAsync(CancellationToken ct = default)
    {
        var releases = await Http.GetFromJsonAsync<List<GitHubRelease>>(ReleasesApi, ct) ?? [];
        return releases
            .Where(r => !r.Draft && Version.TryParse(r.TagName.TrimStart('v'), out _))
            .Select(r => (Version: Version.Parse(r.TagName.TrimStart('v')), Apk: r.Assets.FirstOrDefault(a => a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))))
            .Where(r => r.Apk is not null && r.Version > Current && IsOurDownload(r.Apk.Url))
            .OrderByDescending(r => r.Version)
            .Select(r => new Update(r.Version, r.Apk!.Url, r.Apk.Size))
            .FirstOrDefault();
    }

    /// <summary>Only .apk files attached to this repository's releases, over HTTPS (GitHub redirects them to its file host).</summary>
    private static bool IsOurDownload(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host == "github.com"
        && uri.AbsolutePath.StartsWith("/Infinity7915/Night-loop-Android/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether Android lets Night-loop install updates ("Install unknown apps").</summary>
    public static bool CanInstall(Context context) => context.PackageManager!.CanRequestPackageInstalls();

    public static void OpenInstallPermission(Activity activity) =>
        activity.StartActivity(new Intent(global::Android.Provider.Settings.ActionManageUnknownAppSources,
            global::Android.Net.Uri.Parse($"package:{activity.PackageName}")));

    /// <summary>Downloads the update and starts Android's installer. Reports progress 0–100.</summary>
    public static async Task InstallAsync(Context context, Update update, Action<int> progress, CancellationToken ct = default)
    {
        var folder = System.IO.Path.Combine(context.CacheDir!.AbsolutePath, "updates");
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.GetFiles(folder)) File.Delete(old);
        var file = System.IO.Path.Combine(folder, $"Night-loop-{update.Version}.apk");

        if (!IsOurDownload(update.ApkUrl)) throw new InvalidOperationException("That update doesn't come from Night-loop's releases.");
        using (var response = await Http.GetAsync(update.ApkUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? update.Size;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress((int)(done * 100 / total));
            }
        }

        var installer = context.PackageManager!.PackageInstaller;
        var parameters = new PackageInstaller.SessionParams(PackageInstallMode.FullInstall);
        var sessionId = installer.CreateSession(parameters);
        using var session = installer.OpenSession(sessionId);
        var length = new FileInfo(file).Length;
        await using (var source = File.OpenRead(file))
        await using (var target = session.OpenWrite("base.apk", 0, length))
        {
            await source.CopyToAsync(target, ct);
            session.Fsync(target);
        }
        // Android adds the result to this intent, so it must be mutable (the flag exists from Android 12).
        var flags = OperatingSystem.IsAndroidVersionAtLeast(31)
            ? PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Mutable
            : PendingIntentFlags.UpdateCurrent;
        var result = PendingIntent.GetBroadcast(context, sessionId, new Intent(context, typeof(UpdateInstallResult)), flags)!;
        session.Commit(result.IntentSender);
    }
}

/// <summary>Android's answer to an update install: show its confirm screen, or say why it failed.</summary>
[BroadcastReceiver(Name = "win.nightloop.app.UpdateInstallResult", Exported = false)]
public sealed class UpdateInstallResult : BroadcastReceiver
{
    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent is null) return;
        var status = (PackageInstallStatus)intent.GetIntExtra(PackageInstaller.ExtraStatus, (int)PackageInstallStatus.Failure);
        switch (status)
        {
            case PackageInstallStatus.PendingUserAction:
#pragma warning disable CS0618, CA1422 // the typed overload is Android 13+ only
                var confirm = OperatingSystem.IsAndroidVersionAtLeast(33)
                    ? intent.GetParcelableExtra(Intent.ExtraIntent, Java.Lang.Class.FromType(typeof(Intent))) as Intent
                    : intent.GetParcelableExtra(Intent.ExtraIntent) as Intent;
#pragma warning restore CS0618, CA1422
                if (confirm is null) return;
                confirm.AddFlags(ActivityFlags.NewTask);
                context.StartActivity(confirm);
                break;
            case PackageInstallStatus.Success:
                break;
            case PackageInstallStatus.FailureConflict or PackageInstallStatus.FailureIncompatible:
                Ui.Toast(context, "This update can't install over your Night-loop (different signing key). Uninstall Night-loop, then install it from the releases page.");
                context.StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(Updater.ReleasesPage)).AddFlags(ActivityFlags.NewTask));
                break;
            case PackageInstallStatus.FailureAborted:
                break; // you tapped Cancel
            default:
                Ui.Toast(context, $"The update didn't install: {intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? status.ToString()}");
                break;
        }
    }
}

/// <summary>
/// After an update installs, Android closes the old Night-loop and doesn't reopen it. This shows a notice
/// ("Night-loop updated, tap to open") so it doesn't look like nothing happened.
/// </summary>
[BroadcastReceiver(Name = "win.nightloop.app.UpdatedReceiver", Exported = true)]
[IntentFilter([Intent.ActionMyPackageReplaced])]
public sealed class UpdatedReceiver : BroadcastReceiver
{
    private const string Channel = "updates";

    public override void OnReceive(Context? context, Intent? intent)
    {
        if (context is null || intent?.Action != Intent.ActionMyPackageReplaced) return;
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.CreateNotificationChannel(new NotificationChannel(Channel, "Updates", NotificationImportance.High));
        var open = PendingIntent.GetActivity(context, 1,
            new Intent(context, typeof(MainActivity)).AddFlags(ActivityFlags.NewTask), PendingIntentFlags.Immutable);
        var version = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.VersionName;
        var notification = new Notification.Builder(context, Channel)
            .SetContentTitle($"Night-loop updated to {version}")
            .SetContentText("Tap to open it.")
            .SetSmallIcon(Resource.Drawable.ic_nightloop)
            .SetContentIntent(open)
            .SetAutoCancel(true)
            .Build();
        manager.Notify(7001, notification);
    }
}
