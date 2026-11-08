using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Mobile.Core;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile;

/// <summary>
/// Makes a macro right on top of the game: + puts a numbered dot on the screen, drag it where the tap should land,
/// tap the dot to choose Click or Hold, how long, and how long to wait before the next one. Dots play in number
/// order. The macro is stored as ordinary blocks (Tap / Long press, then Wait), so the app's editor shows it too.
/// </summary>
internal sealed class DotEditor
{
    private const int DotDp = 46;
    private const int DefaultWaitMs = 500;

    /// <summary>One numbered dot: a tap (or hold) and the wait after it.</summary>
    private sealed class Dot
    {
        public string TapId = "";
        public string? WaitId;
        public bool Hold;
        public int Count = 1;
        public int PressMs = 50;
        public int WaitMs = DefaultWaitMs;
        public View View = null!;
        public TextView Label = null!;
        public WindowManagerLayoutParams Layout = null!;
    }

    private readonly BubbleService _service;
    private readonly IWindowManager _windows;
    private readonly Guid _id;
    private readonly List<Dot> _dots = [];
    private MacroDoc _doc;
    private string _name;
    private DraftAutosaver _saver;
    private Dot? _selected;
    private View? _toolbar;
    private WindowManagerLayoutParams? _toolbarLayout;
    private TextView? _status;
    private View? _card;
    private bool _hidden;
    private bool _closed;

    public Guid MacroId => _id;
    public event Action? Closed;

    private DotEditor(BubbleService service, IWindowManager windows, MacroDto macro, MacroDoc doc)
    {
        _service = service;
        _windows = windows;
        _id = macro.Id;
        _name = macro.Name;
        _doc = doc;
        _saver = new DraftAutosaver(Night.Api, macro);
        _saver.StateChanged += _ => Night.OnMain(ShowStatus);
    }

    private Context C => _service;
    private int Px(float dp) => Palette.Dp(C, dp);

    /// <summary>
    /// Opens a macro for dot editing. Returns why it can't (a macro with loops, conditions or other blocks is
    /// edited in the app instead), or null when the editor is open.
    /// </summary>
    public static async Task<(DotEditor? Editor, string? Error)> OpenAsync(BubbleService service, IWindowManager windows, Guid id)
    {
        try
        {
            var macro = await Night.Api.GetMacroAsync(id);
            if (macro.Platform != MacroPlatforms.Android) return (null, "PC macros are edited in Night-loop on your PC.");
            var doc = MacroJson.Deserialize(MacroJson.Serialize(macro.Current ?? new MacroDoc { Name = macro.Name }));
            doc.Platform = MacroPlatforms.Android;
            if (!CanShowAsDots(doc))
                return (null, "This macro uses blocks the bubble can't show as dots (like loops or Open app). Edit it in the Night-loop app.");
            var editor = new DotEditor(service, windows, macro, doc);
            Night.OnMain(editor.Show);
            return (editor, null);
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Only taps and long presses, each followed by at most one wait.</summary>
    private static bool CanShowAsDots(MacroDoc doc)
    {
        var lastWasTouch = false;
        foreach (var b in doc.Blocks)
        {
            if (b.Type is BlockTypes.Tap or BlockTypes.LongPress) lastWasTouch = true;
            else if (b.Type == BlockTypes.Wait && lastWasTouch) lastWasTouch = false;
            else return false;
        }
        return true;
    }

    // ---- Showing ----

    private void Show()
    {
        foreach (var b in _doc.Blocks)
        {
            if (b.Type == BlockTypes.Wait)
            {
                _dots[^1].WaitId = b.Id;
                _dots[^1].WaitMs = b.GetInt("ms", 0);
                continue;
            }
            var dot = new Dot
            {
                TapId = b.Id,
                Hold = b.Type == BlockTypes.LongPress,
                Count = b.GetInt("count", 1),
                PressMs = b.GetInt("holdMs", b.Type == BlockTypes.LongPress ? 600 : 50),
                WaitMs = 0,
            };
            _dots.Add(dot);
            AddDotView(dot, b.GetInt("x", 0), b.GetInt("y", 0));
        }
        ShowToolbar();
        ShowStatus();
    }

    private void ShowToolbar()
    {
        var bar = Ui.Column(C, Px(8));
        bar.Background = Ui.Rounded(C, Palette.Surface, 14, Palette.Border);
        var title = Ui.Text(C, $"✥ {_name}", 14, Palette.Text, bold: true);
        title.SetSingleLine(true);
        title.Ellipsize = TextUtils.TruncateAt.End;
        bar.AddView(title);
        _status = Ui.Text(C, "", 12, Palette.TextQuiet);
        bar.AddView(_status);
        var row = Ui.Row(C);
        row.AddView(Ui.Small(C, "+", AddDot, "Add a dot"));
        row.AddView(Ui.Small(C, "−", RemoveDot, "Remove the selected dot"));
        row.AddView(Ui.Small(C, "▶", () => _ = TestAsync(), "Test the macro"));
        row.AddView(Ui.Small(C, "⚙", ShowMacroCard, "Repeat and name"));
        row.AddView(Ui.Small(C, "✓", () => _ = CloseAsync(), "Save and close"));
        ((LinearLayout.LayoutParams)row.GetChildAt(0)!.LayoutParameters!).LeftMargin = 0;
        bar.AddView(row);

        var metrics = C.Resources!.DisplayMetrics!;
        _toolbarLayout = new WindowManagerLayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay, WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Math.Max(0, metrics.WidthPixels / 2 - Px(130)),
            Y = Px(40),
        };
        // Drag the toolbar by its title when it covers the spot you need.
        title.SetOnTouchListener(new Drag(this, _toolbarLayout, null));
        _windows.AddView(bar, _toolbarLayout);
        _toolbar = bar;
    }

    private void ShowStatus()
    {
        if (_status is null) return;
        var state = _saver.State switch
        {
            SyncState.Saved => "saved",
            SyncState.Pending or SyncState.Saving => "saving…",
            SyncState.Draft => "draft",
            SyncState.Offline => "offline",
            SyncState.Conflict => "changed elsewhere",
            _ => _saver.Message ?? "not saved",
        };
        var repeat = _doc.Options.Repeat.Mode == RepeatModes.Forever ? "repeats forever" : $"plays {_doc.Options.Repeat.Count ?? 1}×";
        _status.Text = $"{_dots.Count} dots · {repeat} · {state}";
    }

    private void AddDotView(Dot dot, int centerX, int centerY)
    {
        var size = Px(DotDp);
        var label = new TextView(C) { Gravity = GravityFlags.Center };
        label.SetTextColor(Color.White);
        label.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        label.SetTextSize(global::Android.Util.ComplexUnitType.Sp, 16);
        dot.Label = label;
        dot.View = label;
        dot.Layout = new WindowManagerLayoutParams(size, size, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.LayoutInScreen, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = centerX - size / 2,
            Y = centerY - size / 2,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            dot.Layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        label.SetOnTouchListener(new Drag(this, dot.Layout, dot));
        _windows.AddView(label, dot.Layout);
        Paint(dot);
    }

    private void Paint(Dot dot)
    {
        var number = _dots.IndexOf(dot) + 1;
        dot.Label.Text = dot.Hold ? $"{number}H" : dot.Count > 1 ? $"{number}×{dot.Count}" : number.ToString();
        var circle = new GradientDrawable();
        circle.SetShape(ShapeType.Oval);
        circle.SetColor(Color.Argb(210, 124, 92, 246));
        circle.SetStroke(Px(dot == _selected ? 3 : 2), dot == _selected ? Color.White : Color.Argb(200, 237, 233, 254));
        dot.Label.Background = circle;
    }

    private void PaintAll()
    {
        foreach (var dot in _dots) Paint(dot);
        ShowStatus();
    }

    // ---- Editing ----

    private void AddDot()
    {
        CloseCard();
        var metrics = C.Resources!.DisplayMetrics!;
        var step = Px(28) * (_dots.Count % 6);
        var dot = new Dot { TapId = NextId(), WaitMs = _dots.LastOrDefault()?.WaitMs ?? DefaultWaitMs };
        _dots.Add(dot);
        _selected = dot;
        AddDotView(dot, metrics.WidthPixels / 2 + step, metrics.HeightPixels / 2 + step);
        PaintAll();
        Changed();
    }

    private void RemoveDot()
    {
        CloseCard();
        var dot = _selected ?? _dots.LastOrDefault();
        if (dot is null) return;
        _windows.RemoveView(dot.View);
        _dots.Remove(dot);
        _selected = null;
        PaintAll();
        Changed();
    }

    private void Select(Dot dot)
    {
        _selected = dot;
        PaintAll();
        ShowDotCard(dot);
    }

    private void Moved() => Changed();

    /// <summary>The settings card for one dot: Click or Hold, taps, press time, wait after.</summary>
    private void ShowDotCard(Dot dot)
    {
        CloseCard();
        var card = Card($"Dot {_dots.IndexOf(dot) + 1}");

        var kind = new RadioGroup(C) { Orientation = Orientation.Horizontal };
        var click = Radio("Click");
        var hold = Radio("Hold");
        kind.AddView(click);
        kind.AddView(hold);
        (dot.Hold ? hold : click).Checked = true;
        card.AddView(kind);

        var countLabel = Ui.Text(C, "Taps (1–3)", 13, Palette.TextQuiet);
        var count = Ui.Number(C, "1", dot.Count);
        card.AddView(countLabel);
        card.AddView(count);
        var pressLabel = Ui.Text(C, "", 13, Palette.TextQuiet);
        var press = Ui.Number(C, "", dot.PressMs);
        card.AddView(pressLabel);
        card.AddView(press);
        card.AddView(Ui.Text(C, "Then wait before the next dot (ms)", 13, Palette.TextQuiet));
        var wait = Ui.Number(C, "0", dot.WaitMs);
        card.AddView(wait);

        void Kind()
        {
            var isHold = hold.Checked;
            countLabel.Visibility = count.Visibility = isHold ? ViewStates.Gone : ViewStates.Visible;
            pressLabel.Text = isHold ? "Hold for (ms), then release" : "Press each tap for (ms)";
            if (isHold && dot.PressMs < 200 && !dot.Hold) press.Text = "600";
            if (!isHold && dot.Hold) press.Text = "50";
        }
        click.CheckedChange += (_, _) => Kind();
        Kind();

        var buttons = Ui.Row(C);
        buttons.AddView(Ui.Button(C, "Done", () =>
        {
            dot.Hold = hold.Checked;
            dot.Count = Math.Clamp(int.TryParse(count.Text, out var n) ? n : 1, MacroLimits.ClicksMin, MacroLimits.ClicksMax);
            dot.PressMs = Math.Clamp(int.TryParse(press.Text, out var p) ? p : dot.Hold ? 600 : 50, 1, MacroLimits.TouchHoldMaxMs);
            dot.WaitMs = Math.Clamp(int.TryParse(wait.Text, out var w) ? w : 0, 0, MacroLimits.WaitMaxMs);
            CloseCard();
            PaintAll();
            Changed();
        }));
        buttons.AddView(Ui.Button(C, "Delete", () =>
        {
            _selected = dot;
            RemoveDot();
        }, primary: false));
        card.AddView(buttons);
        ShowCard(card, dot.Layout.X, dot.Layout.Y + Px(DotDp + 8));
    }

    /// <summary>Repeat count and name.</summary>
    private void ShowMacroCard()
    {
        CloseCard();
        var card = Card("Macro");
        card.AddView(Ui.Text(C, "Name", 13, Palette.TextQuiet));
        var name = Ui.Input(C, "Name", _name);
        card.AddView(name);
        card.AddView(Ui.Text(C, "Play it how many times? (blank = Infinite)", 13, Palette.TextQuiet));
        var times = Ui.Number(C, "Infinite", _doc.Options.Repeat.Mode == RepeatModes.Forever ? null : _doc.Options.Repeat.Count ?? 1);
        card.AddView(times);
        var buttons = Ui.Row(C);
        buttons.AddView(Ui.Button(C, "Done", () =>
        {
            _doc.Options.Repeat = int.TryParse(times.Text, out var n) && n > 0
                ? new RepeatOptions { Mode = RepeatModes.Count, Count = Math.Min(n, MacroLimits.CountMax) }
                : new RepeatOptions { Mode = RepeatModes.Forever, Count = null };
            var newName = name.Text?.Trim() ?? "";
            CloseCard();
            Changed();
            if (newName.Length > 0 && newName != _name) _ = RenameAsync(newName);
        }));
        buttons.AddView(Ui.Button(C, "Cancel", CloseCard, primary: false));
        card.AddView(buttons);
        ShowCard(card, _toolbarLayout?.X ?? 0, (_toolbarLayout?.Y ?? 0) + Px(120));
    }

    private LinearLayout Card(string title)
    {
        var card = Ui.Column(C, Px(14));
        card.Background = Ui.Rounded(C, Palette.Surface, 14, Palette.Accent);
        card.AddView(Ui.Text(C, title, 16, Palette.Text, bold: true));
        return card;
    }

    private RadioButton Radio(string text)
    {
        var radio = new RadioButton(C) { Text = text, Id = View.GenerateViewId() };
        radio.SetTextColor(Palette.Text);
        return radio;
    }

    /// <summary>Cards take the keyboard (so they're focusable), but touches outside still reach the game.</summary>
    private void ShowCard(View card, int x, int y)
    {
        var metrics = C.Resources!.DisplayMetrics!;
        var width = Math.Min(Px(300), metrics.WidthPixels - Px(16));
        var layout = new WindowManagerLayoutParams(width, ViewGroup.LayoutParams.WrapContent, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Math.Clamp(x, Px(8), Math.Max(Px(8), metrics.WidthPixels - width - Px(8))),
            Y = Math.Clamp(y, Px(8), Math.Max(Px(8), metrics.HeightPixels - Px(380))),
            SoftInputMode = SoftInput.AdjustPan | SoftInput.StateHidden,
        };
        _windows.AddView(card, layout);
        _card = card;
    }

    private void CloseCard()
    {
        if (_card is not null) _windows.RemoveView(_card);
        _card = null;
    }

    // ---- Saving and playing ----

    private void Changed()
    {
        _doc.Blocks = ToBlocks();
        _saver.Changed(_doc);
        ShowStatus();
    }

    /// <summary>Dots → blocks, keeping each dot's block ids so saving doesn't count them as new blocks.</summary>
    private List<Block> ToBlocks()
    {
        var blocks = new List<Block>();
        foreach (var dot in _dots)
        {
            var (x, y) = Center(dot);
            var p = new System.Text.Json.Nodes.JsonObject { ["x"] = x, ["y"] = y, ["holdMs"] = dot.PressMs };
            if (!dot.Hold && dot.Count > 1) p["count"] = dot.Count;
            blocks.Add(Block.Create(dot.TapId, dot.Hold ? BlockTypes.LongPress : BlockTypes.Tap, p));
            if (dot.WaitMs > 0)
            {
                dot.WaitId ??= NextId(blocks);
                blocks.Add(Block.Create(dot.WaitId, BlockTypes.Wait, new() { ["ms"] = dot.WaitMs }));
            }
        }
        return blocks;
    }

    /// <summary>
    /// The dot's middle in screen pixels, where the tap lands. Dot windows are laid out in screen coordinates
    /// (LayoutInScreen + LayoutNoLimits, into the cutout), so the window position is the screen position, even right
    /// after a drag when the view hasn't been laid out again yet.
    /// </summary>
    private (int X, int Y) Center(Dot dot)
    {
        var half = Px(DotDp) / 2;
        return (Math.Max(0, dot.Layout.X + half), Math.Max(0, dot.Layout.Y + half));
    }

    private string NextId(List<Block>? building = null)
    {
        var used = new HashSet<string>(_dots.SelectMany(d => new[] { d.TapId, d.WaitId }).OfType<string>(), StringComparer.Ordinal);
        if (building is not null) used.UnionWith(building.Select(b => b.Id));
        for (var i = 1; ; i++)
            if (!used.Contains($"b{i}")) return $"b{i}";
    }

    /// <summary>Hides the dots and toolbar (so taps reach the game), plays, then brings them back.</summary>
    private async Task TestAsync()
    {
        CloseCard();
        if (_dots.Count == 0)
        {
            Ui.Toast(C, "Tap + to add a dot first.");
            return;
        }
        Changed();
        var copy = MacroJson.Deserialize(MacroJson.Serialize(_doc));
        SetHidden(true);
        if (Night.Play(_id, copy) is { } error)
        {
            SetHidden(false);
            Ui.Toast(C, error);
            return;
        }
        Ui.Toast(C, "Playing. Tap the bubble to stop.");
        if (Night.RunOf(_id) is { } run)
        {
            try { await run.Completion; }
            catch (Exception) { /* faults are shown by Night */ }
        }
        Night.OnMain(() =>
        {
            if (!_closed) SetHidden(false);
        });
    }

    private void SetHidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _hidden = hidden;
        var state = hidden ? ViewStates.Gone : ViewStates.Visible;
        foreach (var dot in _dots) dot.View.Visibility = state;
        if (_toolbar is not null) _toolbar.Visibility = state;
    }

    private async Task RenameAsync(string name)
    {
        try
        {
            var updated = await Night.Api.UpdateMacroAsync(_id, new UpdateMacroRequest(Name: name));
            Night.OnMain(() =>
            {
                _name = updated.Name;
                _doc.Name = updated.Name;
                if (_toolbar is LinearLayout bar && bar.GetChildAt(0) is TextView title) title.Text = $"✥ {_name}";
            });
            _ = Night.RefreshMacrosAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            Night.OnMain(() => Ui.Toast(C, ex.Message));
        }
    }

    /// <summary>Saves (so the bubble's ▶ plays the new version) and takes everything off the screen.</summary>
    public async Task CloseAsync()
    {
        if (_closed) return;
        CloseCard();
        Changed();
        var issues = MacroValidator.Validate(_doc);
        try
        {
            if (issues.Count == 0) await _saver.SaveAsync(_doc);
            else await _saver.FlushAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            Night.OnMain(() => Ui.Toast(C, $"Not saved: {ex.Message}"));
            return;
        }
        Night.OnMain(() =>
        {
            Ui.Toast(C, issues.Count == 0 ? $"Saved \"{_name}\"." : $"Kept as a draft: {issues[0].Message}");
            Dismiss();
        });
        _ = Night.RefreshMacrosAsync();
    }

    /// <summary>Removes every view right away (the bubble is going away).</summary>
    public void Dismiss()
    {
        if (_closed) return;
        _closed = true;
        CloseCard();
        foreach (var dot in _dots)
            if (dot.View.IsAttachedToWindow) _windows.RemoveView(dot.View);
        if (_toolbar is not null) _windows.RemoveView(_toolbar);
        _toolbar = null;
        _ = _saver.DisposeAsync().AsTask();
        Closed?.Invoke();
    }

    /// <summary>Drags a dot (or the toolbar). A touch that hardly moves is a tap: it opens the dot's settings.</summary>
    private sealed class Drag(DotEditor editor, WindowManagerLayoutParams layout, Dot? dot) : Java.Lang.Object, View.IOnTouchListener
    {
        private float _downX, _downY;
        private int _startX, _startY;
        private bool _moved;

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (v is null || e is null) return false;
            var target = dot?.View ?? editor._toolbar;
            switch (e.Action)
            {
                case MotionEventActions.Down:
                    (_downX, _downY, _startX, _startY, _moved) = (e.RawX, e.RawY, layout.X, layout.Y, false);
                    return true;
                case MotionEventActions.Move:
                    var (dx, dy) = (e.RawX - _downX, e.RawY - _downY);
                    if (Math.Abs(dx) > 10 || Math.Abs(dy) > 10) _moved = true;
                    if (_moved && target is not null)
                    {
                        layout.X = _startX + (int)dx;
                        layout.Y = _startY + (int)dy;
                        editor._windows.UpdateViewLayout(target, layout);
                    }
                    return true;
                case MotionEventActions.Up:
                    if (dot is null) return true;
                    if (_moved) editor.Moved();
                    else editor.Select(dot);
                    return true;
            }
            return false;
        }
    }
}
