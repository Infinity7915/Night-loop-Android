using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;

namespace NightLoop.Mobile.Screens;

/// <summary>The Night colors from the PC app's default theme.</summary>
internal static class Palette
{
    public static readonly Color Background = Color.ParseColor("#0F0B1A");
    public static readonly Color Surface = Color.ParseColor("#1A1430");
    public static readonly Color SurfaceHigh = Color.ParseColor("#241C40");
    public static readonly Color Border = Color.ParseColor("#2E2550");
    public static readonly Color Accent = Color.ParseColor("#A78BFA");
    public static readonly Color Text = Color.ParseColor("#EDE9FE");
    public static readonly Color TextQuiet = Color.ParseColor("#A59BC4");
    public static readonly Color Good = Color.ParseColor("#4ADE80");
    public static readonly Color Warn = Color.ParseColor("#FBBF24");
    public static readonly Color Bad = Color.ParseColor("#F87171");

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
        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal };
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
        button.SetMinHeight(Palette.Dp(c, 40));
        button.SetMinimumHeight(Palette.Dp(c, 40));
        button.SetPadding(Palette.Dp(c, 14), 0, Palette.Dp(c, 14), 0);
        button.Click += (_, _) => onClick();
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
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
        button.SetMinWidth(size);
        button.SetMinimumWidth(size);
        button.SetMinHeight(size);
        button.SetMinimumHeight(size);
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
        return input;
    }

    public static EditText Number(Context c, string hint, int? value) =>
        Input(c, hint, value?.ToString() ?? "", InputTypes.ClassNumber);

    public static View Fill(View view)
    {
        view.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1);
        return view;
    }

    public static ScrollView Scroll(Context c, View content)
    {
        var scroll = new ScrollView(c) { FillViewport = true };
        scroll.SetBackgroundColor(Palette.Background);
        scroll.AddView(content);
        return scroll;
    }

    public static void Toast(Context c, string text) =>
        global::Android.Widget.Toast.MakeText(c, text, ToastLength.Long)!.Show();
}
