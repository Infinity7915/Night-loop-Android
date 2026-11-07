using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using NightLoop.Mobile.Setup;

namespace NightLoop.Mobile;

/// <summary>
/// Phase 1 home screen: the setup checklist (the permissions Night-loop needs, each with a button to the right
/// Android setting), the floating bubble switch and a server check. Sign-in and macros come in phase 2.
/// </summary>
[Activity(Name = "win.nightloop.app.MainActivity", Label = "Night-loop", MainLauncher = true, Exported = true,
          LaunchMode = LaunchMode.SingleTop, Icon = "@drawable/ic_nightloop",
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private LinearLayout _steps = null!;
    private Button _bubbleButton = null!;
    private TextView _bubbleNote = null!;
    private TextView _server = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AppInfo.Load(this);

        var page = Ui.Column(this, Palette.Dp(this, 16));
        page.AddView(Ui.Text(this, "Night-loop", 26, Palette.Text, bold: true));
        page.AddView(Ui.Text(this, $"Android preview {AppInfo.Version}", 14, Palette.TextQuiet));

        page.AddView(Ui.Heading(this, "Set up"));
        _steps = Ui.Column(this, 0);
        page.AddView(_steps);

        page.AddView(Ui.Heading(this, "Floating bubble"));
        var bubbleCard = Ui.Card(this);
        _bubbleNote = Ui.Text(this, "", 14, Palette.TextQuiet);
        _bubbleButton = Ui.Button(this, "Show bubble", ToggleBubble);
        bubbleCard.AddView(_bubbleNote);
        bubbleCard.AddView(_bubbleButton);
        page.AddView(bubbleCard);

        page.AddView(Ui.Heading(this, "Server"));
        var serverCard = Ui.Card(this);
        _server = Ui.Text(this, "Checking…", 14, Palette.TextQuiet);
        serverCard.AddView(_server);
        page.AddView(serverCard);

        page.AddView(Ui.Text(this, "Signing in and playing macros from the bubble come in the next update.", 13, Palette.TextQuiet));

        var scroll = new ScrollView(this);
        scroll.SetBackgroundColor(Palette.Background);
        scroll.AddView(page);
        SetContentView(scroll);
        _ = CheckServerAsync();
    }

    protected override void OnResume()
    {
        base.OnResume();
        Refresh();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        Refresh();
    }

    private void Refresh()
    {
        _steps.RemoveAllViews();
        foreach (var step in Permissions.Steps(this))
        {
            var card = Ui.Card(this);
            var top = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var title = Ui.Text(this, step.Title, 16, Palette.Text, bold: true);
            top.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
            top.AddView(Ui.Text(this, step.Done ? "On" : "Off", 14, step.Done ? Palette.Good : Palette.Warn, bold: true));
            card.AddView(top);
            card.AddView(Ui.Text(this, step.Why, 14, Palette.TextQuiet));
            if (!step.Done) card.AddView(Ui.Button(this, "Turn on", () => step.Open(this)));
            _steps.AddView(card);
        }

        var canDraw = Permissions.CanDrawOverlays(this);
        _bubbleButton.Enabled = canDraw;
        _bubbleButton.Text = BubbleService.Running ? "Hide bubble" : "Show bubble";
        _bubbleNote.Text = canDraw
            ? "The Night-loop bubble stays on top of other apps and the home screen. Drag it anywhere; tap it to come back here."
            : "Turn on \"Display over other apps\" above to use the bubble.";
    }

    private void ToggleBubble()
    {
        var intent = new Intent(this, typeof(BubbleService));
        if (BubbleService.Running)
            StartService(intent.SetAction(BubbleService.ActionStop));
        else
            StartForegroundService(intent);
        // The service flips Running when it starts or stops.
        _bubbleButton.PostDelayed(Refresh, 300);
    }

    private async Task CheckServerAsync()
    {
        var text = await ServerCheck.DescribeAsync(this);
        RunOnUiThread(() => _server.Text = text);
    }
}

/// <summary>The Night colors from the PC app's default theme.</summary>
internal static class Palette
{
    public static readonly Color Background = Color.ParseColor("#0F0B1A");
    public static readonly Color Surface = Color.ParseColor("#1A1430");
    public static readonly Color Border = Color.ParseColor("#2E2550");
    public static readonly Color Accent = Color.ParseColor("#A78BFA");
    public static readonly Color Text = Color.ParseColor("#EDE9FE");
    public static readonly Color TextQuiet = Color.ParseColor("#A59BC4");
    public static readonly Color Good = Color.ParseColor("#4ADE80");
    public static readonly Color Warn = Color.ParseColor("#FBBF24");

    public static int Dp(Context context, float dp) => (int)(dp * context.Resources!.DisplayMetrics!.Density + 0.5f);
}

/// <summary>Small view builders, so the phase 1 screen needs no layout files.</summary>
internal static class Ui
{
    public static LinearLayout Column(Context c, int padding)
    {
        var column = new LinearLayout(c) { Orientation = Orientation.Vertical };
        column.SetPadding(padding, padding, padding, padding);
        return column;
    }

    public static TextView Text(Context c, string text, float sp, Color color, bool bold = false)
    {
        var view = new TextView(c) { Text = text };
        view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, sp);
        view.SetTextColor(color);
        if (bold) view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        view.SetPadding(0, Palette.Dp(c, 2), 0, Palette.Dp(c, 2));
        return view;
    }

    public static TextView Heading(Context c, string text)
    {
        var view = Text(c, text, 13, Palette.Accent, bold: true);
        view.SetPadding(0, Palette.Dp(c, 20), 0, Palette.Dp(c, 6));
        return view;
    }

    public static LinearLayout Card(Context c)
    {
        var card = Column(c, Palette.Dp(c, 14));
        var shape = new GradientDrawable();
        shape.SetColor(Palette.Surface);
        shape.SetCornerRadius(Palette.Dp(c, 12));
        shape.SetStroke(Palette.Dp(c, 1), Palette.Border);
        card.Background = shape;
        var margins = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        margins.SetMargins(0, 0, 0, Palette.Dp(c, 10));
        card.LayoutParameters = margins;
        return card;
    }

    public static Button Button(Context c, string text, Action onClick)
    {
        var button = new Button(c) { Text = text };
        button.SetAllCaps(false);
        button.SetTextColor(Palette.Background);
        var shape = new GradientDrawable();
        shape.SetColor(Palette.Accent);
        shape.SetCornerRadius(Palette.Dp(c, 10));
        button.Background = shape;
        button.Click += (_, _) => onClick();
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        layout.SetMargins(0, Palette.Dp(c, 10), 0, 0);
        button.LayoutParameters = layout;
        return button;
    }
}
