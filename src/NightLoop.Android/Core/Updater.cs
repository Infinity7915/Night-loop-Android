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
    /// <summary>Android's last answer to an update install, shown under Update now so a failed update says why.</summary>
    public static string? LastResult(Context context) =>
        context.GetSharedPreferences("nightloop", FileCreationMode.Private)!.GetString("last_install", null);

    public static void SaveResult(Context context, string text) =>
        context.GetSharedPreferences("nightloop", FileCreationMode.Private)!.Edit()!.PutString("last_install", text)!.Apply();

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

        // Hand the file to Android's own installer, the same screen as opening the .apk yourself
        // (an install session showed the confirm screen but then did nothing on some phones).
        var uri = global::Android.Net.Uri.Parse($"content://{UpdateFileProvider.Authority}/{System.IO.Path.GetFileName(file)}")!;
        var view = new Intent(Intent.ActionView)
            .SetDataAndType(uri, "application/vnd.android.package-archive")
            .AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(view);
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
        if (status != PackageInstallStatus.PendingUserAction)
            Updater.SaveResult(context, $"{DateTime.Now:HH:mm} {status}: {intent.GetStringExtra(PackageInstaller.ExtraStatusMessage) ?? "no message"}");
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
                Ui.Toast(context, "The update was cancelled.");
                break;
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

/// <summary>Shares only the downloaded update .apk (cache/updates) with Android's installer, read-only.</summary>
[ContentProvider([UpdateFileProvider.Authority], Name = "win.nightloop.app.UpdateFileProvider", Exported = false, GrantUriPermissions = true)]
public sealed class UpdateFileProvider : ContentProvider
{
    public const string Authority = "win.nightloop.app.updates";

    public override bool OnCreate() => true;

    public override global::Android.OS.ParcelFileDescriptor? OpenFile(global::Android.Net.Uri uri, string mode)
    {
        if (mode != "r") throw new Java.Lang.SecurityException("Read only.");
        var name = uri.LastPathSegment ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^Night-loop-[0-9.]+\.apk$"))
            throw new Java.IO.FileNotFoundException(name);
        var file = new Java.IO.File(new Java.IO.File(Context!.CacheDir, "updates"), name);
        return global::Android.OS.ParcelFileDescriptor.Open(file, global::Android.OS.ParcelFileMode.ReadOnly);
    }

    public override string? GetType(global::Android.Net.Uri uri) => "application/vnd.android.package-archive";
    public override global::Android.Database.ICursor? Query(global::Android.Net.Uri uri, string[]? projection, string? selection, string[]? selectionArgs, string? sortOrder) => null;
    public override global::Android.Net.Uri? Insert(global::Android.Net.Uri uri, ContentValues? values) => null;
    public override int Delete(global::Android.Net.Uri uri, string? selection, string[]? selectionArgs) => 0;
    public override int Update(global::Android.Net.Uri uri, ContentValues? values, string? selection, string[]? selectionArgs) => 0;
}
