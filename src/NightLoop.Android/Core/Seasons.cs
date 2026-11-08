using Android.Content;
using Android.Content.PM;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Seasonal themes (Settings switch, off by default): around holidays Night-loop's icon and bubble get a seasonal look.
/// The home-screen icon is switched with activity aliases in AndroidManifest.xml (one per season, plus the normal one).
/// </summary>
internal static class Seasons
{
    public static readonly string[] All = ["christmas", "newyear", "valentine", "stpatrick", "easter", "july4", "halloween", "thanksgiving", "lunarnewyear", "midautumn", "diwali", "holi", "eid", "dayofthedead", "carnival", "hanami", "canadaday", "bastille", "bonfire", "australiaday", "kingsday", "oktoberfest"];

    private static readonly string[] EastAsia = ["CN", "HK", "TW", "MO", "SG", "MY", "KR", "VN"];
    private static readonly string[] StPatrickCountries = ["US", "IE", "GB", "CA", "AU", "NZ"];
    private static readonly string[] DiwaliCountries = ["IN", "NP", "SG", "MY", "LK", "FJ", "MU", "TT", "GY", "SR"];
    private static readonly string[] HoliCountries = ["IN", "NP"];
    private static readonly string[] EidCountries =
    [
        "SA", "AE", "QA", "KW", "BH", "OM", "YE", "JO", "IQ", "SY", "LB", "PS", "EG", "LY", "TN", "DZ", "MA", "SD",
        "TR", "IR", "AF", "PK", "BD", "ID", "MY", "BN", "MV", "SN", "ML", "NE", "SO", "DJ", "AZ", "KZ", "UZ", "TJ", "KG", "TM",
    ];
    private static readonly string[] OktoberfestCountries = ["DE", "AT"];
    private static readonly string[] HalloweenCountries = ["US", "CA", "GB", "IE", "AU", "NZ"];

    // Holidays that move with the moon: their dates for the coming years.
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
    private static readonly Dictionary<int, (int M, int D)> Diwali = new()
    {
        [2026] = (11, 8), [2027] = (10, 29), [2028] = (10, 17), [2029] = (11, 5), [2030] = (10, 26),
        [2031] = (11, 14), [2032] = (11, 2), [2033] = (10, 22), [2034] = (11, 10), [2035] = (10, 30),
    };
    private static readonly Dictionary<int, (int M, int D)> Holi = new()
    {
        [2026] = (3, 4), [2027] = (3, 22), [2028] = (3, 11), [2029] = (3, 1), [2030] = (3, 20),
        [2031] = (3, 9), [2032] = (3, 27), [2033] = (3, 16), [2034] = (3, 5), [2035] = (3, 24),
    };
    private static readonly DateTime[] EidAlFitr =
    [
        new(2026, 3, 20), new(2027, 3, 9), new(2028, 2, 26), new(2029, 2, 14), new(2030, 2, 4), new(2031, 1, 24),
        new(2032, 1, 14), new(2033, 1, 2), new(2033, 12, 23), new(2034, 12, 12), new(2035, 12, 1),
    ];

    /// <summary>
    /// The season for this date (or null on ordinary days). Normally only holidays of this country show (two-letter
    /// region code from the device's region setting, never GPS, never saved or sent). With <paramref name="global"/>
    /// every country's holidays show. When holidays overlap, the one whose main day is closest wins; when their main
    /// days are within 3 days of each other it's a coin flip, the same one all day.
    /// </summary>
    public static string? For(DateTime day, string? region, bool global = false)
    {
        var country = string.IsNullOrWhiteSpace(region) ? "US" : region.Trim().ToUpperInvariant();
        var d = day.Date;
        var candidates = new List<(string Name, DateTime Main)>();
        foreach (var y in new[] { d.Year - 1, d.Year, d.Year + 1 })
            foreach (var (name, main, before, after, countries) in Holidays(y))
            {
                if (d < main.AddDays(-before) || d > main.AddDays(after)) continue;
                var here = countries is null ? !NoShared(name, country) : countries.Contains(country);
                if (here || global) candidates.Add((name, main));
            }
        if (candidates.Count == 0) return null;
        candidates.Sort((a, b) => Math.Abs((a.Main - d).Days).CompareTo(Math.Abs((b.Main - d).Days)));
        var best = candidates[0];
        var ties = candidates.Where(c => Math.Abs((c.Main - best.Main).Days) <= 3 && Math.Abs((c.Main - d).Days) - Math.Abs((best.Main - d).Days) <= 3).ToList();
        if (ties.Count == 1) return best.Name;
        // Same pick for the whole day, so the icon doesn't change every time the app opens.
        var seed = d.Year * 1000 + d.DayOfYear;
        return ties.OrderBy(t => t.Name, StringComparer.Ordinal).ElementAt(new Random(seed).Next(ties.Count)).Name;
    }

    /// <summary>Shared holidays that a country doesn't usually celebrate.</summary>
    private static bool NoShared(string name, string country) => name switch
    {
        "christmas" => EidCountries.Contains(country),
        "easter" => EastAsia.Contains(country) || EidCountries.Contains(country),
        _ => false,
    };

    /// <summary>Every holiday in a year: its main day, days shown before and after, and its countries (null = everywhere).</summary>
    private static IEnumerable<(string Name, DateTime Main, int Before, int After, string[]? Countries)> Holidays(int y)
    {
        var easter = EasterSunday(y);
        yield return ("newyear", new DateTime(y, 1, 1), 1, 1, null);
        yield return ("christmas", new DateTime(y, 12, 25), 7, 5, null);
        yield return ("valentine", new DateTime(y, 2, 14), 6, 0, null);
        yield return ("easter", easter, 7, 1, null);
        yield return ("stpatrick", new DateTime(y, 3, 17), 6, 0, StPatrickCountries);
        yield return ("halloween", new DateTime(y, 10, 31), 7, 0, HalloweenCountries);
        yield return ("july4", new DateTime(y, 7, 4), 6, 1, ["US"]);
        yield return ("thanksgiving", NthWeekday(y, 11, DayOfWeek.Thursday, 4), 3, 3, ["US"]);
        yield return ("thanksgiving", NthWeekday(y, 10, DayOfWeek.Monday, 2), 0, 6, ["CA"]);
        yield return ("canadaday", new DateTime(y, 7, 1), 3, 0, ["CA"]);
        yield return ("dayofthedead", new DateTime(y, 11, 1), 1, 1, ["MX"]);
        yield return ("carnival", easter.AddDays(-47), 3, 1, ["BR"]);
        yield return ("hanami", new DateTime(y, 4, 1), 7, 6, ["JP"]);
        yield return ("bastille", new DateTime(y, 7, 14), 4, 0, ["FR"]);
        yield return ("bonfire", new DateTime(y, 11, 5), 4, 0, ["GB"]);
        yield return ("australiaday", new DateTime(y, 1, 26), 3, 0, ["AU"]);
        yield return ("kingsday", new DateTime(y, 4, 27), 3, 0, ["NL"]);
        yield return ("oktoberfest", new DateTime(y, 9, 26), 7, 8, OktoberfestCountries);
        if (LunarNewYear.TryGetValue(y, out var lny)) yield return ("lunarnewyear", new DateTime(y, lny.M, lny.D), 1, 6, EastAsia);
        if (MidAutumn.TryGetValue(y, out var ma)) yield return ("midautumn", new DateTime(y, ma.M, ma.D), 3, 1, EastAsia);
        if (Diwali.TryGetValue(y, out var dw)) yield return ("diwali", new DateTime(y, dw.M, dw.D), 2, 2, DiwaliCountries);
        if (Holi.TryGetValue(y, out var ho)) yield return ("holi", new DateTime(y, ho.M, ho.D), 1, 1, HoliCountries);
        foreach (var eid in EidAlFitr.Where(e => e.Year == y)) yield return ("eid", eid, 1, 2, EidCountries);
    }

    private static DateTime NthWeekday(int y, int month, DayOfWeek weekday, int n)
    {
        var t = new DateTime(y, month, 1);
        while (t.DayOfWeek != weekday) t = t.AddDays(1);
        return t.AddDays(7 * (n - 1));
    }

    /// <summary>The season to show now: null when the switch is off or it's an ordinary day.</summary>
    public static string? Now => LocalSettings.SeasonalThemes || LocalSettings.GlobalSeasonalThemes ? For(DateTime.Now, Region(), LocalSettings.GlobalSeasonalThemes) : null;

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
        "diwali" => Resource.Drawable.ic_nightloop_diwali,
        "holi" => Resource.Drawable.ic_nightloop_holi,
        "eid" => Resource.Drawable.ic_nightloop_eid,
        "dayofthedead" => Resource.Drawable.ic_nightloop_dayofthedead,
        "carnival" => Resource.Drawable.ic_nightloop_carnival,
        "hanami" => Resource.Drawable.ic_nightloop_hanami,
        "canadaday" => Resource.Drawable.ic_nightloop_canadaday,
        "bastille" => Resource.Drawable.ic_nightloop_bastille,
        "bonfire" => Resource.Drawable.ic_nightloop_bonfire,
        "australiaday" => Resource.Drawable.ic_nightloop_australiaday,
        "kingsday" => Resource.Drawable.ic_nightloop_kingsday,
        "oktoberfest" => Resource.Drawable.ic_nightloop_oktoberfest,
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
