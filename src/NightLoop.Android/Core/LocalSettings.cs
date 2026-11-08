using Android.Content;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile.Core;

/// <summary>Settings kept on this phone only (like the PC app's LocalSettings): the theme and the start delay.</summary>
internal static class LocalSettings
{
    public const int StartDelayDefaultMs = 700;
    public const int StartDelayMaxMs = 5000;

    private static ISharedPreferences? _prefs;

    public static void Load(Context context)
    {
        _prefs = context.GetSharedPreferences("nightloop", FileCreationMode.Private);
        Palette.Apply(_prefs?.GetString("theme", null));
    }

    public static string Theme
    {
        get => Palette.Current.Id;
        set
        {
            Palette.Apply(value);
            _prefs?.Edit()!.PutString("theme", Palette.Current.Id)!.Apply();
        }
    }

    /// <summary>How long Play waits before the first step, so the game is in front and ready for the first tap.</summary>
    public static int StartDelayMs
    {
        get => Math.Clamp(_prefs?.GetInt("start_delay_ms", StartDelayDefaultMs) ?? StartDelayDefaultMs, 0, StartDelayMaxMs);
        set => _prefs?.Edit()!.PutInt("start_delay_ms", Math.Clamp(value, 0, StartDelayMaxMs))!.Apply();
    }
}
