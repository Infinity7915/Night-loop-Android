using Android.Content;
using Android.Runtime;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Keeps the last crash on this phone (no personal data, never sent anywhere) so Night-loop can show it next time
/// and you can copy it into a bug report.
/// </summary>
internal static class CrashLog
{
    private static string? _path;

    public static void Install(Context app)
    {
        _path = System.IO.Path.Combine(app.FilesDir!.AbsolutePath, "last-crash.txt");
        AndroidEnvironment.UnhandledExceptionRaiser += (_, e) => Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Write(e.Exception); e.SetObserved(); };
    }

    private static void Write(Exception? ex)
    {
        if (ex is null || _path is null) return;
        try
        {
            File.WriteAllText(_path,
                $"Night-loop for Android {AppInfo.Version}\n" +
                $"Android {global::Android.OS.Build.VERSION.Release} (API {(int)global::Android.OS.Build.VERSION.SdkInt}), " +
                $"{global::Android.OS.Build.Manufacturer} {global::Android.OS.Build.Model}\n" +
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n\n{ex}");
        }
        catch (IOException) { }
    }

    /// <summary>The saved crash, or null.</summary>
    public static string? Last => _path is not null && File.Exists(_path) ? File.ReadAllText(_path) : null;

    public static void Clear()
    {
        if (_path is not null && File.Exists(_path)) File.Delete(_path);
    }
}
