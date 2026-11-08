using Android.App;
using Android.Content;
using Android.Views;

namespace NightLoop.Mobile.Screens;

/// <summary>
/// Pop-up windows that work from a screen of the app and from the bubble. The bubble is a service, so its windows
/// are drawn over other apps (it has "Display over other apps").
/// </summary>
internal static class Dialogs
{
    /// <summary>A dialog in the light or dark style that matches the chosen theme.</summary>
    public static AlertDialog.Builder Builder(Context c) => new(c, Style);

    /// <summary>A context for building views that go in a dialog: the screen itself, or for the bubble the dialog style.</summary>
    public static Context Themed(Context c) => c is Activity ? c : new ContextThemeWrapper(c, Style);

    private static int Style => Palette.IsLight
        ? global::Android.Resource.Style.ThemeMaterialLightDialogAlert
        : global::Android.Resource.Style.ThemeMaterialDialogAlert;

    public static AlertDialog Show(Context c, AlertDialog.Builder builder)
    {
        var dialog = builder.Create()!;
        if (c is not Activity) dialog.Window?.SetType(WindowManagerTypes.ApplicationOverlay);
        dialog.Show();
        return dialog;
    }
}
