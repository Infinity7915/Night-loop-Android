using Android.Content;
using Android.Content.PM;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Seasonal themes (Settings switch, off by default): around holidays Night-loop's icon and bubble get a seasonal look.
/// The home-screen icon is switched with activity aliases in AndroidManifest.xml (one per season, plus the normal one).
/// </summary>
internal static class Seasons
{
    public static readonly string[] All = ["christmas", "newyear", "valentine", "stpatrick", "easter", "july4", "halloween", "thanksgiving"];

    /// <summary>The season for this date, or null on ordinary days.</summary>
    public static string? For(DateTime day)
    {
        var d = day.Date;
        var y = d.Year;
        bool In(DateTime from, DateTime to) => d >= from && d <= to;
        if (In(new(y, 12, 31), new(y, 12, 31)) || In(new(y, 1, 1), new(y, 1, 2))) return "newyear";
        if (In(new(y, 12, 18), new(y, 12, 30))) return "christmas";
        if (In(new(y, 2, 8), new(y, 2, 14))) return "valentine";
        if (In(new(y, 3, 11), new(y, 3, 17))) return "stpatrick";
        var easter = EasterSunday(y);
        if (In(easter.AddDays(-7), easter.AddDays(1))) return "easter";
        if (In(new(y, 6, 28), new(y, 7, 5))) return "july4";
        if (In(new(y, 10, 24), new(y, 10, 31))) return "halloween";
        var thanksgiving = new DateTime(y, 11, 1);
        while (thanksgiving.DayOfWeek != DayOfWeek.Thursday) thanksgiving = thanksgiving.AddDays(1);
        thanksgiving = thanksgiving.AddDays(21);
        if (In(thanksgiving.AddDays(-3), thanksgiving.AddDays(3))) return "thanksgiving"; // Monday to Sunday
        return null;
    }

    /// <summary>The season to show now: null when the switch is off or it's an ordinary day.</summary>
    public static string? Now => LocalSettings.SeasonalThemes ? For(DateTime.Now) : null;

    /// <summary>The icon to draw in the app (bubble, notifications).</summary>
    public static int Icon => Now switch
    {
        "christmas" => Resource.Drawable.ic_nightloop_christmas,
        "newyear" => Resource.Drawable.ic_nightloop_newyear,
        "valentine" => Resource.Drawable.ic_nightloop_valentine,
        "stpatrick" => Resource.Drawable.ic_nightloop_stpatrick,
        "easter" => Resource.Drawable.ic_nightloop_easter,
        "july4" => Resource.Drawable.ic_nightloop_july4,
        "halloween" => Resource.Drawable.ic_nightloop_halloween,
        "thanksgiving" => Resource.Drawable.ic_nightloop_thanksgiving,
        _ => Resource.Drawable.ic_nightloop,
    };

    /// <summary>Turns on the home-screen icon for the current season (or the normal one) and turns the others off.</summary>
    public static void ApplyLauncherIcon(Context context)
    {
        try
        {
            var pm = context.PackageManager!;
            var want = Now;
            foreach (var name in All.Prepend(null))
            {
                var component = new ComponentName(context.PackageName!, name is null ? "win.nightloop.app.Launcher" : $"win.nightloop.app.Launcher_{name}");
                var state = name == want ? ComponentEnabledState.Enabled : ComponentEnabledState.Disabled;
                var current = pm.GetComponentEnabledSetting(component);
                var on = current == ComponentEnabledState.Enabled || (current == ComponentEnabledState.Default && name is null);
                if (on == (state == ComponentEnabledState.Enabled)) continue;
                pm.SetComponentEnabledSetting(component, state, ComponentEnableOption.DontKillApp);
            }
        }
        catch (Java.Lang.Exception) { /* some launchers refuse; the in-app icons still change */ }
    }

    // Anonymous Gregorian algorithm.
    private static DateTime EasterSunday(int y)
    {
        int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451, month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(y, month, day);
    }
}
