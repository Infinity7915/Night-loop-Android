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

    /// <summary>Bubble switch: the numbered dots disappear while a macro plays (they come back when it stops).</summary>
    public static bool HideDotsWhilePlaying
    {
        get => _prefs?.GetBoolean("hide_dots_playing", false) ?? false;
        set => _prefs?.Edit()!.PutBoolean("hide_dots_playing", value)!.Apply();
    }

<<<<<<< Updated upstream
=======
    /// <summary>Settings switch: seasonal icons around holidays (see <see cref="Seasons"/>). Off until turned on.</summary>
    public static bool SeasonalThemes
    {
        get => _prefs?.GetBoolean("seasonal_themes", false) ?? false;
        set => _prefs?.Edit()!.PutBoolean("seasonal_themes", value)!.Apply();
    }

    /// <summary>Settings switch: seasonal icons for every country's holidays (instead of <see cref="SeasonalThemes"/>; only one is on).</summary>
    public static bool GlobalSeasonalThemes
    {
        get => _prefs?.GetBoolean("global_seasonal_themes", false) ?? false;
        set => _prefs?.Edit()!.PutBoolean("global_seasonal_themes", value)!.Apply();
    }

    /// <summary>Settings switch: while a macro plays with the ☰ step list open, the playing step is highlighted and kept centered.</summary>
    public static bool FollowPlayingStep
    {
        get => _prefs?.GetBoolean("follow_playing_step", true) ?? true;
        set => _prefs?.Edit()!.PutBoolean("follow_playing_step", value)!.Apply();
    }

>>>>>>> Stashed changes
    /// <summary>Bubble switch: the play bar (toolbar) disappears while a macro plays. Stop it from the bubble.</summary>
    public static bool HidePlayBarWhilePlaying
    {
        get => _prefs?.GetBoolean("hide_bar_playing", false) ?? false;
        set => _prefs?.Edit()!.PutBoolean("hide_bar_playing", value)!.Apply();
    }

    public const int WaitAfterMaxMs = 2000;

    /// <summary>Settings › Wait after taps (ms): after Tap, Long press and Swipe unless the block sets its own.</summary>
    public static int WaitAfterTapsMs
    {
        get => Math.Clamp(_prefs?.GetInt("wait_after_taps_ms", 0) ?? 0, 0, WaitAfterMaxMs);
        set { _prefs?.Edit()!.PutInt("wait_after_taps_ms", Math.Clamp(value, 0, WaitAfterMaxMs))!.Apply(); Night.Waits.AfterClicksMs = value; }
    }

    /// <summary>Settings › Wait after every block (ms), like the PC app's.</summary>
    public static int WaitAfterBlocksMs
    {
        get => Math.Clamp(_prefs?.GetInt("wait_after_blocks_ms", 0) ?? 0, 0, WaitAfterMaxMs);
        set { _prefs?.Edit()!.PutInt("wait_after_blocks_ms", Math.Clamp(value, 0, WaitAfterMaxMs))!.Apply(); Night.Waits.AfterBlocksMs = value; }
    }
}
