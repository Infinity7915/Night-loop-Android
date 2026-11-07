using Android.Content;
using NightLoop.Client;

namespace NightLoop.Mobile;

/// <summary>Asks the Night-loop server which Android versions it accepts, using the shared API client.</summary>
internal static class ServerCheck
{
    public const string ServerUrl = "https://api.night-loop.win/v1/";

    public static async Task<string> DescribeAsync(Context context)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(ServerUrl), Timeout = TimeSpan.FromSeconds(20) };
            var api = new ApiClient(http, AppInfo.InstallId(context), AppInfo.Version, Contracts.MacroDocument.MacroPlatforms.Android);
            var version = await api.VersionAsync();
            return $"Connected to api.night-loop.win. Newest Android version: {version.Latest}.";
        }
        catch (Exception ex)
        {
            return $"Couldn't reach the server ({ex.GetType().Name}). Check your internet connection.";
        }
    }
}
