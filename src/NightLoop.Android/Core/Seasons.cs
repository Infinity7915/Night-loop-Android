using Android.Content;
using Android.Content.PM;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Seasonal themes (Settings switch, off by default): around holidays Night-loop's icon and bubble get a seasonal look.
/// The home-screen icon is switched with activity aliases in AndroidManifest.xml (one per season, plus the normal one).
/// </summary>
internal static class Seasons
{
    public static readonly string[] All = ["christmas", "newyear", "valentine", "stpatrick", "easter", "july4", "halloween", "thanksgiving", "lunarnewyear", "midautumn"];

    private static readonly string[] EastAsia = ["CN", "HK", "TW", "MO", "SG", "MY", "KR", "VN"];
    private static readonly string[] StPatrickCountries = ["US", "IE", "GB", "CA", "AU", "NZ"];

    // Lunar New Year and Mid-Autumn move with the moon: their dates for the coming years.
    private static readonly Dictionary<int, (int M, int D)> LunarNewYear = new()
    {
        [2026] = (2, 17), [2027] = (2, 6), [2028] = (1, 26), [2029] = (2, 13), [2030] = (2, 3),
        [2031] = (1, 23), [2032] = (2, 11), [2033] = (1, 31), [2034] = (2, 19), [2035] = (2, 8),
    };
    private static readonly Dictionary<int, (int M, int D)> MidAutumn = new()
    {
        [2026] = (9, 25), [2027] = (9, 15), [2028] = (10, 3), [2029] = (9, 22), [2030] = (9, 12),
        [2031] = (10, 1), [2032] = (9, 19), [2033] = (9, 8), [2034] = (9, 27), [2035] = (9, 16),
    };

    /// <summary>
    /// The season for this date in this country (two-letter region code from the device's region setting, never
    /// GPS, and never saved or sent anywhere), or null on ordinary days.
    /// </summary>
    public static string? For(DateTime day, string? region)
    {
        var country = string.IsNullOrWhiteSpace(region) ? "US" : region.Trim().ToUpperInvariant();
        var eastAsia = EastAsia.Contains(country);
        var d = day.Date;
        var y = d.Year;
        bool In(DateTime from, DateTime to) => d >= from && d <= to;
        if (eastAsia && LunarNewYear.TryGetValue(y, out var lny))
        {
            var start = new DateTime(y, lny.M, lny.D);
            if (In(start.AddDays(-1), start.AddDays(6))) return "lunarnewyear";
        }
        if (eastAsia && MidAutumn.TryGetValue(y, out var ma))
        {
            var full = new DateTime(y, ma.M, ma.D);
            if (In(full.AddDays(-3), full.AddDays(1))) return "midautumn";
        }
        if (d == new DateTime(y, 12, 31) || In(new(y, 1, 1), new(y, 1, 2))) return "newyear";
        if (In(new(y, 12, 18), new(y, 12, 30))) return "christmas";
        if (In(new(y, 2, 8), new(y, 2, 14))) return "valentine";
        if (StPatrickCountries.Contains(country) && In(new(y, 3, 11), new(y, 3, 17))) return "stpatrick";
        var easter = EasterSunday(y);
        if (!eastAsia && In(easter.AddDays(-7), easter.AddDays(1))) return "easter";
        if (country == "US" && In(new(y, 6, 28), new(y, 7, 5))) return "july4";
        if (!eastAsia && In(new(y, 10, 24), new(y, 10, 31))) return "halloween";
        if (country is "US" or "CA")
        {
            // US: 4th Thursday of November. Canada: 2nd Monday of October. The whole week around it, Monday to Sunday.
            var t = country == "US" ? new DateTime(y, 11, 1) : new DateTime(y, 10, 1);
            var weekday = country == "US" ? DayOfWeek.Thursday : DayOfWeek.Monday;
            while (t.DayOfWeek != weekday) t = t.AddDays(1);
            t = t.AddDays(country == "US" ? 21 : 7);
            var monday = t.AddDays(-(((int)t.DayOfWeek + 6) % 7));
            if (In(monday, monday.AddDays(6))) return "thanksgiving";
        }
        return null;
    }

    /// <summary>The season to show now: null when the switch is off or it's an ordinary day.</summary>
    public static string? Now => LocalSettings.SeasonalThemes ? For(DateTime.Now, Region()) : null;

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
        "lunarnewyear" => Resource.Drawable.ic_nightloop_lunarnewyear,
        "midautumn" => Resource.Drawable.ic_nightloop_midautumn,
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

    private static string? Region()
    {
        try { return Java.Util.Locale.Default.Country; }
        catch (Exception) { return null; }
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
