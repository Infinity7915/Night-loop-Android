using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace NightLoop.Mobile.Screens;

/// <summary>
/// The colors of the chosen theme, the same themes as the PC app (Settings › Theme). Night, the original purple, is
/// the default. Screens are rebuilt after a change; the bubble picks it up the next time it opens something.
/// </summary>
internal static class Palette
{
    public sealed record Theme(string Id, string Name, bool Light, string Background, string Surface, string SurfaceHigh,
        string Border, string Accent, string Text, string TextQuiet);

    public static readonly Theme[] Themes =
    [
        new("night", "Night", false, "#0F0B1A", "#1A1430", "#241C40", "#2E2550", "#A78BFA", "#EDE9FE", "#A59BC4"),
        new("rose", "Rose", false, "#1A0E15", "#24141E", "#301B28", "#44283B", "#F472B6", "#F7EAF1", "#B693A8"),
        new("bloodmoon", "Blood moon", false, "#140A0A", "#1F1010", "#2B1616", "#422020", "#E5484D", "#F5E9E7", "#B08E89"),
        new("ocean", "Ocean", false, "#0A121A", "#101C28", "#172736", "#22384C", "#38BDF8", "#E8F1F8", "#8AA3B8"),
        new("cyber", "Cyber", false, "#0A0A12", "#11111F", "#1A1A2E", "#2A2A48", "#E4FF3A", "#EEF0FF", "#8F94BF"),
        new("lunar", "Lunar", false, "#111317", "#191C22", "#22262E", "#31363F", "#C7D2FE", "#EDEFF3", "#969CA8"),
        new("vanilla", "Vanilla", true, "#FAF2DC", "#FFF8E4", "#F4EACB", "#E4D5AC", "#C8892B", "#2B2414", "#75694A"),
    ];

    public static Theme Current { get; private set; } = Themes[0];
    public static bool IsLight => Current.Light;

    public static Color Background { get; private set; }
    public static Color Surface { get; private set; }
    public static Color SurfaceHigh { get; private set; }
    public static Color Border { get; private set; }
    public static Color Accent { get; private set; }
    public static Color Text { get; private set; }
    public static Color TextQuiet { get; private set; }
    public static Color Good { get; private set; }
    public static Color Warn { get; private set; }
    public static Color Bad { get; private set; }

    static Palette() => Apply(null);

    /// <summary>Switches to a theme by id (unknown or null = Night).</summary>
    public static void Apply(string? id)
    {
        var t = Themes.FirstOrDefault(x => x.Id == id) ?? Themes[0];
        Current = t;
        Background = Color.ParseColor(t.Background);
        Surface = Color.ParseColor(t.Surface);
        SurfaceHigh = Color.ParseColor(t.SurfaceHigh);
        Border = Color.ParseColor(t.Border);
        Accent = Color.ParseColor(t.Accent);
        Text = Color.ParseColor(t.Text);
        TextQuiet = Color.ParseColor(t.TextQuiet);
        // Darker status colors on the light theme, so they stay readable.
        Good = Color.ParseColor(t.Light ? "#15803D" : "#4ADE80");
        Warn = Color.ParseColor(t.Light ? "#B45309" : "#FBBF24");
        Bad = Color.ParseColor(t.Light ? "#B91C1C" : "#F87171");
    }

    public static int Dp(Context context, float dp) => (int)(dp * context.Resources!.DisplayMetrics!.Density + 0.5f);
}

/// <summary>Small view builders, so the screens need no layout files.</summary>
internal static class Ui
{
    public static LinearLayout Column(Context c, int padding)
    {
        var column = new LinearLayout(c) { Orientation = Orientation.Vertical };
        column.SetPadding(padding, padding, padding, padding);
        return column;
    }

    public static LinearLayout Row(Context c)
    {
        // Not baseline-aligned: lining up text baselines pushed buttons down and cut off their bottoms.
        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal, BaselineAligned = false };
        row.SetGravity(GravityFlags.CenterVertical);
        return row;
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

    public static GradientDrawable Rounded(Context c, Color fill, float radiusDp, Color? stroke = null)
    {
        var shape = new GradientDrawable();
        shape.SetColor(fill);
        shape.SetCornerRadius(Palette.Dp(c, radiusDp));
        if (stroke is { } s) shape.SetStroke(Palette.Dp(c, 1), s);
        return shape;
    }

    public static LinearLayout Card(Context c)
    {
        var card = Column(c, Palette.Dp(c, 14));
        card.Background = Rounded(c, Palette.Surface, 12, Palette.Border);
        var margins = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        margins.SetMargins(0, 0, 0, Palette.Dp(c, 10));
        card.LayoutParameters = margins;
        return card;
    }

    /// <summary>A filled purple button (primary) or an outlined one.</summary>
    public static Button Button(Context c, string text, Action onClick, bool primary = true)
    {
        var button = new Button(c) { Text = text };
        button.SetAllCaps(false);
        button.SetTextColor(primary ? Palette.Background : Palette.Text);
        button.Background = primary ? Rounded(c, Palette.Accent, 10) : Rounded(c, Palette.SurfaceHigh, 10, Palette.Border);
        button.SetMinHeight(0);
        button.SetMinimumHeight(0);
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.StateListAnimator = null; // no shadow that spills outside the card
        button.Gravity = GravityFlags.Center;
        button.SetPadding(Palette.Dp(c, 16), 0, Palette.Dp(c, 16), 0);
        button.Click += (_, _) => onClick();
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Palette.Dp(c, 44));
        layout.SetMargins(0, Palette.Dp(c, 10), Palette.Dp(c, 8), 0);
        button.LayoutParameters = layout;
        return button;
    }

    /// <summary>A small square text button for row actions (▶, ↑, ✕).</summary>
    public static Button Small(Context c, string text, Action onClick, string description)
    {
        var button = new Button(c) { Text = text, ContentDescription = description };
        button.SetAllCaps(false);
        button.SetTextColor(Palette.Text);
        button.Background = Rounded(c, Palette.SurfaceHigh, 8, Palette.Border);
        var size = Palette.Dp(c, 40);
        button.SetMinWidth(0);
        button.SetMinimumWidth(0);
        button.SetMinHeight(0);
        button.SetMinimumHeight(0);
        button.StateListAnimator = null;
        button.Gravity = GravityFlags.Center;
        button.SetPadding(0, 0, 0, 0);
        button.Click += (_, _) => onClick();
        var layout = new LinearLayout.LayoutParams(size, size);
        layout.SetMargins(Palette.Dp(c, 6), 0, 0, 0);
        button.LayoutParameters = layout;
        return button;
    }

    public static EditText Input(Context c, string hint, string value = "", InputTypes type = InputTypes.ClassText)
    {
        var input = new EditText(c) { Hint = hint, Text = value, InputType = type };
        input.SetTextColor(Palette.Text);
        input.SetHintTextColor(Palette.TextQuiet);
        input.SetSingleLine(!type.HasFlag(InputTypes.TextFlagMultiLine));
        input.Background = Rounded(c, Palette.Background, 8, Palette.Border);
        input.SetPadding(Palette.Dp(c, 12), Palette.Dp(c, 10), Palette.Dp(c, 12), Palette.Dp(c, 10));
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        layout.SetMargins(0, Palette.Dp(c, 6), 0, 0);
        input.LayoutParameters = layout;
        // One tap opens the keyboard (over a game the first tap only focused the box), and its key says Done
        // instead of jumping on to the next box, so each value is edited on its own.
        input.ImeOptions = global::Android.Views.InputMethods.ImeAction.Done | (global::Android.Views.InputMethods.ImeAction)0x02000000;
        input.FocusChange += (_, e) => { if (e.HasFocus) ShowKeyboard(input); };
        input.Click += (_, _) => ShowKeyboard(input);
        input.EditorAction += (_, e) =>
        {
            if (e.ActionId != global::Android.Views.InputMethods.ImeAction.Done) { e.Handled = false; return; }
            HideKeyboard(input);
            input.ClearFocus();
            e.Handled = true;
        };
        return input;
    }

    private static void ShowKeyboard(EditText input) => input.Post(() =>
    {
        input.RequestFocus();
        (input.Context!.GetSystemService(Context.InputMethodService) as global::Android.Views.InputMethods.InputMethodManager)
            ?.ShowSoftInput(input, global::Android.Views.InputMethods.ShowFlags.Implicit);
    });

    private static void HideKeyboard(EditText input) =>
        (input.Context!.GetSystemService(Context.InputMethodService) as global::Android.Views.InputMethods.InputMethodManager)
            ?.HideSoftInputFromWindow(input.WindowToken, 0);

    public static EditText Number(Context c, string hint, int? value) =>
        Input(c, hint, value?.ToString() ?? "", InputTypes.ClassNumber);

    /// <summary>
    /// A number box that also takes the word Infinite (any case, or just "inf"), so it opens the full keyboard.
    /// Blank counts as Infinite too; see <see cref="SaysInfinite"/>.
    /// </summary>
    public static EditText NumberOrInfinite(Context c, string hint, int? value) =>
        Input(c, hint, value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            InputTypes.ClassText | InputTypes.TextFlagNoSuggestions);

    /// <summary>True for a blank box or one that says Infinite (or starts with "inf", or ∞).</summary>
    public static bool SaysInfinite(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length == 0 || t == "∞" || t.StartsWith("inf", StringComparison.OrdinalIgnoreCase);
    }

    public static View Fill(View view)
    {
        view.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
        return view;
    }

    /// <summary>Widest a page gets: on tablets and phones held sideways the column stays readable, centered.</summary>
    public const int PageMaxWidthDp = 720;

    public static ScrollView Scroll(Context c, View content)
    {
        var scroll = new ScrollView(c) { FillViewport = true };
        scroll.SetBackgroundColor(Palette.Background);
        var frame = new FrameLayout(c);
        content.LayoutParameters = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent,
            GravityFlags.CenterHorizontal);
        frame.AddView(content);
        // Cap the width once the frame knows how wide the screen is (and again after turning the phone).
        frame.LayoutChange += (_, e) =>
        {
            var max = Palette.Dp(c, PageMaxWidthDp);
            var width = e.Right - e.Left;
            var want = width > max ? max : ViewGroup.LayoutParams.MatchParent;
            var lp = (FrameLayout.LayoutParams)content.LayoutParameters!;
            if (lp.Width == want) return;
            lp.Width = want;
            content.Post(content.RequestLayout);
        };
        scroll.AddView(frame, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        scroll.SetOnApplyWindowInsetsListener(new KeepClearOfBars());
        return scroll;
    }

    /// <summary>
    /// Android 15 draws apps behind the status bar, navigation bar and keyboard. Pad the page so nothing hides
    /// under them (the title sat under the clock before).
    /// </summary>
    private sealed class KeepClearOfBars : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.Ime() | WindowInsets.Type.DisplayCutout());
                v.SetPadding(bars.Left, bars.Top, bars.Right, bars.Bottom);
            }
            else
            {
#pragma warning disable CS0618, CA1422 // the only way before Android 11
                v.SetPadding(insets.SystemWindowInsetLeft, insets.SystemWindowInsetTop, insets.SystemWindowInsetRight, insets.SystemWindowInsetBottom);
#pragma warning restore CS0618, CA1422
            }
            return insets;
        }
    }

    public static void Toast(Context c, string text) =>
        global::Android.Widget.Toast.MakeText(c, text, ToastLength.Long)!.Show();
}
