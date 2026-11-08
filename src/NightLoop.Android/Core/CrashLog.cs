using Android.Content;
using Android.OS;
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
        // A forgotten background error (like a dropped connection) doesn't close the app, so it isn't a crash.
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
    }

    private static void Write(Exception? ex)
    {
        if (ex is null || _path is null) return;
        try
        {
            File.WriteAllText(_path,
                $"Night-loop for Android {AppInfo.Version}\n" +
                $"Android {Build.VERSION.Release} (API {(int)Build.VERSION.SdkInt}), " +
                $"{Build.Manufacturer} {Build.Model}\n" +
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
