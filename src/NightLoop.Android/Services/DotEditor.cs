using System.Text.Json.Nodes;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Text;
using Android.Views;
using Android.Widget;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Runtime;
using NightLoop.Mobile.Core;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile;

/// <summary>
/// Makes a macro right on top of the game, as numbered steps that play in order:
/// <list type="bullet">
/// <item>a dot (+): drag it where the tap lands; tap it for Click or Hold, how long, and the wait before the next step;</item>
/// <item>a wait (⏱): pause for a time;</item>
/// <item>a key (⌨): type text, or press, hold down or release a key in the focused text box.</item>
/// </list>
/// Steps stay on screen while the macro plays (see-through, taps pass through them) and the playing one lights up.
/// They are stored as ordinary blocks, so the app's editor shows the same macro.
/// </summary>
internal sealed class DotEditor
{
    private const int DotDp = 46;
    private const int DefaultWaitMs = 500;
    /// <summary>Id prefix of the wait that belongs to a dot ("then wait"), so it loads back onto its dot.</summary>
    private const string AfterPrefix = "after-";
    /// <summary>Android lets taps through an app's overlay only when it is at most 80% opaque.</summary>
    private const float PlayingAlpha = 0.55f;

    private enum Kind { Touch, Wait, Key }

    /// <summary>One numbered step.</summary>
    private sealed class Step
    {
        public Kind Kind;
        public string Id = "";
        // Touch
        public bool Hold;
        public int Count = 1;
        public int PressMs = 50;
        public int WaitMs = DefaultWaitMs;
        public string? WaitId;
        // Wait
        public int Ms = 1000;
        // Key: type, press, down, up
        public string KeyMode = "press";
        public string Key = "Enter";
        public string Text = "";
        public int KeyHoldMs = 50;

        public TextView View = null!;
        public WindowManagerLayoutParams Layout = null!;
    }

    private readonly BubbleService _service;
    private readonly IWindowManager _windows;
    private readonly Guid _id;
    private readonly List<Step> _steps = [];
    private readonly Dictionary<string, Step> _byBlockId = [];
    private MacroDoc _doc;
    private string _name;
    private readonly DraftAutosaver _saver;
    private Step? _selected;
    private Step? _playing;
    private LinearLayout? _toolbar;
    private WindowManagerLayoutParams? _toolbarLayout;
    private TextView? _title;
    private TextView? _status;
    private Button? _playButton;
    private View? _card;
    private bool _isPlaying;
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

    /// <summary>Opens a macro's steps on the screen, or says why it can't (it uses blocks steps can't show).</summary>
    public static async Task<(DotEditor? Editor, string? Error)> OpenAsync(BubbleService service, IWindowManager windows, Guid id)
    {
        try
        {
            var macro = await Night.Api.GetMacroAsync(id);
            if (macro.Platform != MacroPlatforms.Android) return (null, "PC macros are edited in Night-loop on your PC.");
            var doc = MacroJson.Deserialize(MacroJson.Serialize(macro.Current ?? new MacroDoc { Name = macro.Name }));
            doc.Platform = MacroPlatforms.Android;
            if (doc.Blocks.Any(b => b.Type is not (BlockTypes.Tap or BlockTypes.LongPress or BlockTypes.Wait
                    or BlockTypes.Key or BlockTypes.HoldKey or BlockTypes.Type)))
                return (null, "This macro uses blocks the bubble can't show as steps (like loops or Open app). Edit it in the Night-loop app.");
            var editor = new DotEditor(service, windows, macro, doc);
            Night.OnMain(editor.Show);
            return (editor, null);
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            return (null, ex.Message);
        }
    }

    // ---- Loading ----

    private void Show()
    {
        Step? lastTouch = null;
        foreach (var b in _doc.Blocks)
        {
            if (b.Type == BlockTypes.Wait && lastTouch is not null && b.Id.StartsWith(AfterPrefix, StringComparison.Ordinal))
            {
                lastTouch.WaitId = b.Id;
                lastTouch.WaitMs = b.GetInt("ms", 0);
                lastTouch = null;
                continue;
            }
            var step = b.Type switch
            {
                BlockTypes.Tap or BlockTypes.LongPress => new Step
                {
                    Kind = Kind.Touch,
                    Hold = b.Type == BlockTypes.LongPress,
                    Count = b.GetInt("count", 1),
                    PressMs = b.GetInt("holdMs", b.Type == BlockTypes.LongPress ? 600 : 50),
                    WaitMs = 0,
                },
                BlockTypes.Wait => new Step { Kind = Kind.Wait, Ms = b.GetInt("ms", 1000) },
                BlockTypes.Type => new Step { Kind = Kind.Key, KeyMode = "type", Text = b.GetString("text", "") },
                BlockTypes.HoldKey => new Step { Kind = Kind.Key, KeyMode = "press", Key = b.GetString("key", "Enter"), KeyHoldMs = b.GetInt("holdMs", 1000) },
                _ => new Step
                {
                    Kind = Kind.Key,
                    KeyMode = b.GetString("action", "press"),
                    Key = b.GetString("key", "Enter"),
                    KeyHoldMs = b.GetInt("holdMs", 50),
                },
            };
            step.Id = b.Id;
            _steps.Add(step);
            lastTouch = step.Kind == Kind.Touch ? step : null;
            if (step.Kind == Kind.Touch) AddView(step, b.GetInt("x", 0), b.GetInt("y", 0));
            else AddView(step, null, null);
        }
        ShowToolbar();
        PaintAll();
        Night.RunsChanged += OnRunsChanged;
    }

    // ---- Toolbar ----

    private void ShowToolbar()
    {
        var bar = Ui.Column(C, Px(8));
        bar.Background = Ui.Rounded(C, Palette.Surface, 14, Palette.Border);
        _title = Ui.Text(C, $"✥ {_name}", 14, Palette.Text, bold: true);
        _title.SetSingleLine(true);
        _title.Ellipsize = TextUtils.TruncateAt.End;
        bar.AddView(_title);
        _status = Ui.Text(C, "", 12, Palette.TextQuiet);
        bar.AddView(_status);
        var row = Ui.Row(C);
        row.AddView(Ui.Small(C, "+", AddDot, "Add a tap dot"));
        row.AddView(Ui.Small(C, "⏱", AddWait, "Add a wait"));
        row.AddView(Ui.Small(C, "⌨", AddKey, "Add a key step"));
        row.AddView(Ui.Small(C, "−", RemoveSelected, "Remove the selected step"));
        _playButton = Ui.Small(C, "▶", () => _ = TogglePlayAsync(), "Play or stop");
        row.AddView(_playButton);
        row.AddView(Ui.Small(C, "⚙", ShowMacroCard, "Name and repeat"));
        row.AddView(Ui.Small(C, "✓", () => _ = CloseAsync(), "Save and close"));
        ((LinearLayout.LayoutParams)row.GetChildAt(0)!.LayoutParameters!).LeftMargin = 0;
        bar.AddView(row);

        var metrics = C.Resources!.DisplayMetrics!;
        _toolbarLayout = new WindowManagerLayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay, WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Math.Max(0, metrics.WidthPixels / 2 - Px(170)),
            Y = Px(40),
        };
        // Drag the toolbar by its title when it covers the spot you need.
        _title.SetOnTouchListener(new Drag(this, _toolbarLayout, null));
        _windows.AddView(bar, _toolbarLayout);
        _toolbar = bar;
    }

    private void ShowStatus()
    {
        if (_status is null) return;
        if (_isPlaying)
        {
            _status.Text = _playing is null ? "Playing…" : $"Playing step {_steps.IndexOf(_playing) + 1} · ■ stops";
            return;
        }
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
        _status.Text = $"{_steps.Count} steps · {repeat} · {state}";
    }

    // ---- Step views ----

    /// <summary>Dots sit where they tap; waits and keys are chips you can park anywhere.</summary>
    private void AddView(Step step, int? centerX, int? centerY)
    {
        var view = new TextView(C) { Gravity = GravityFlags.Center };
        view.SetTextColor(Color.White);
        view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, step.Kind == Kind.Touch ? 16 : 13);
        step.View = view;
        int width, height, x, y;
        if (step.Kind == Kind.Touch)
        {
            width = height = Px(DotDp);
            x = (centerX ?? 0) - width / 2;
            y = (centerY ?? 0) - height / 2;
        }
        else
        {
            width = ViewGroup.LayoutParams.WrapContent;
            height = Px(36);
            view.SetPadding(Px(12), 0, Px(12), 0);
            // Chips line up down the left side, out of the way; drag them anywhere.
            var chips = _steps.Count(s => s.Kind != Kind.Touch && s != step);
            x = Px(8);
            y = Px(150) + chips % 10 * Px(44);
        }
        step.Layout = new WindowManagerLayoutParams(width, height, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.LayoutInScreen, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = x,
            Y = y,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            step.Layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        if (_isPlaying) PassThrough(step, true);
        view.SetOnTouchListener(new Drag(this, step.Layout, step));
        _windows.AddView(view, step.Layout);
    }

    private void Paint(Step step)
    {
        var number = _steps.IndexOf(step) + 1;
        step.View.Text = step.Kind switch
        {
            Kind.Touch => step.Hold ? $"{number}H" : step.Count > 1 ? $"{number}×{step.Count}" : number.ToString(),
            Kind.Wait => $"{number} ⏱ {Duration(step.Ms)}",
            _ => step.KeyMode switch
            {
                "type" => $"{number} ⌨ \"{Short(step.Text)}\"",
                "down" => $"{number} ⌨ {step.Key} ↓",
                "up" => $"{number} ⌨ {step.Key} ↑",
                _ => $"{number} ⌨ {step.Key}",
            },
        };
        var shape = new GradientDrawable();
        if (step.Kind == Kind.Touch) shape.SetShape(ShapeType.Oval);
        else shape.SetCornerRadius(Px(18));
        var fill = step.Kind switch
        {
            Kind.Touch => Color.Argb(210, 124, 92, 246),
            Kind.Wait => Color.Argb(220, 46, 37, 80),
            _ => Color.Argb(220, 30, 64, 120),
        };
        shape.SetColor(step == _playing ? Color.Argb(230, 34, 160, 90) : fill);
        var ring = step == _playing ? Palette.Good : step == _selected ? Color.White : Color.Argb(200, 237, 233, 254);
        shape.SetStroke(Px(step == _selected || step == _playing ? 3 : 2), ring);
        step.View.Background = shape;
    }

    private void PaintAll()
    {
        foreach (var step in _steps) Paint(step);
        ShowStatus();
    }

    private static string Duration(int ms) => ms >= 1000 && ms % 100 == 0 ? $"{ms / 1000.0:0.#} s" : $"{ms} ms";
    private static string Short(string text) => text.Length <= 12 ? text : text[..11] + "…";

    // ---- Adding and removing ----

    private void AddDot()
    {
        CloseCard();
        var metrics = C.Resources!.DisplayMetrics!;
        var touches = _steps.Count(s => s.Kind == Kind.Touch);
        var step = Px(28) * (touches % 6);
        var dot = new Step { Kind = Kind.Touch, Id = NextId(), WaitMs = _steps.LastOrDefault(s => s.Kind == Kind.Touch)?.WaitMs ?? DefaultWaitMs };
        Insert(dot);
        AddView(dot, metrics.WidthPixels / 2 + step, metrics.HeightPixels / 2 + step);
        PaintAll();
        Changed();
    }

    private void AddWait()
    {
        var wait = new Step { Kind = Kind.Wait, Id = NextId() };
        Insert(wait);
        AddView(wait, null, null);
        PaintAll();
        Changed();
        ShowWaitCard(wait);
    }

    private void AddKey()
    {
        var key = new Step { Kind = Kind.Key, Id = NextId() };
        Insert(key);
        AddView(key, null, null);
        PaintAll();
        ShowKeyCard(key);
    }

    /// <summary>New steps go after the selected one, or at the end.</summary>
    private void Insert(Step step)
    {
        var at = _selected is null ? _steps.Count : _steps.IndexOf(_selected) + 1;
        _steps.Insert(at, step);
        _selected = step;
    }

    private void RemoveSelected()
    {
        CloseCard();
        var step = _selected ?? _steps.LastOrDefault();
        if (step is null) return;
        _windows.RemoveView(step.View);
        _steps.Remove(step);
        _selected = null;
        PaintAll();
        Changed();
    }

    private void Select(Step step)
    {
        if (_isPlaying) return;
        _selected = step;
        PaintAll();
        switch (step.Kind)
        {
            case Kind.Touch: ShowDotCard(step); break;
            case Kind.Wait: ShowWaitCard(step); break;
            default: ShowKeyCard(step); break;
        }
    }

    // ---- Cards ----

    private void ShowDotCard(Step dot)
    {
        CloseCard();
        var card = Card($"Step {_steps.IndexOf(dot) + 1}: tap");
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
        card.AddView(Ui.Text(C, "Then wait before the next step (ms)", 13, Palette.TextQuiet));
        var wait = Ui.Number(C, "0", dot.WaitMs);
        card.AddView(wait);

        void Kind()
        {
            var isHold = hold.Checked;
            countLabel.Visibility = count.Visibility = isHold ? ViewStates.Gone : ViewStates.Visible;
            pressLabel.Text = isHold ? "Hold for (ms), then release" : "Press each tap for (ms)";
            if (isHold && !dot.Hold && dot.PressMs < 200) press.Text = "600";
            if (!isHold && dot.Hold) press.Text = "50";
        }
        click.CheckedChange += (_, _) => Kind();
        Kind();

        Buttons(card, () =>
        {
            dot.Hold = hold.Checked;
            dot.Count = Math.Clamp(Int(count, 1), MacroLimits.ClicksMin, MacroLimits.ClicksMax);
            dot.PressMs = Math.Clamp(Int(press, dot.Hold ? 600 : 50), 1, MacroLimits.TouchHoldMaxMs);
            dot.WaitMs = Math.Clamp(Int(wait, 0), 0, MacroLimits.WaitMaxMs);
        }, dot);
        ShowCard(card, dot.Layout.X, dot.Layout.Y + Px(DotDp + 8));
    }

    private void ShowWaitCard(Step step)
    {
        CloseCard();
        var card = Card($"Step {_steps.IndexOf(step) + 1}: wait");
        card.AddView(Ui.Text(C, "Wait for (ms). 1000 ms = 1 second.", 13, Palette.TextQuiet));
        var ms = Ui.Number(C, "1000", step.Ms);
        card.AddView(ms);
        Buttons(card, () => step.Ms = Math.Clamp(Int(ms, 1000), MacroLimits.WaitMinMs, MacroLimits.WaitMaxMs), step);
        ShowCard(card, step.Layout.X + Px(40), step.Layout.Y);
    }

    private void ShowKeyCard(Step step)
    {
        CloseCard();
        var card = Card($"Step {_steps.IndexOf(step) + 1}: keys");
        card.AddView(Ui.Text(C, "Goes to the text box that's selected (Android 13+).", 12, Palette.TextQuiet));
        var modes = new RadioGroup(C) { Orientation = Orientation.Vertical };
        var type = Radio("Type text");
        var press = Radio("Press a key");
        var down = Radio("Hold a key down");
        var up = Radio("Release a key");
        foreach (var r in new[] { type, press, down, up }) modes.AddView(r);
        (step.KeyMode switch { "type" => type, "down" => down, "up" => up, _ => press }).Checked = true;
        card.AddView(modes);

        var textLabel = Ui.Text(C, "Text to type", 13, Palette.TextQuiet);
        var text = Ui.Input(C, "Text", step.Text);
        var keyLabel = Ui.Text(C, "Key (A, 5, Enter, Space, Backspace, Tab, Up, Shift, F1…)", 13, Palette.TextQuiet);
        var key = Ui.Input(C, "Key", step.Key);
        var holdLabel = Ui.Text(C, "Hold it for (ms)", 13, Palette.TextQuiet);
        var hold = Ui.Number(C, "50", step.KeyHoldMs);
        foreach (var v in new View[] { textLabel, text, keyLabel, key, holdLabel, hold }) card.AddView(v);

        void Mode()
        {
            var typing = type.Checked;
            textLabel.Visibility = text.Visibility = typing ? ViewStates.Visible : ViewStates.Gone;
            keyLabel.Visibility = key.Visibility = typing ? ViewStates.Gone : ViewStates.Visible;
            holdLabel.Visibility = hold.Visibility = press.Checked ? ViewStates.Visible : ViewStates.Gone;
        }
        foreach (var r in new[] { type, press, down, up }) r.CheckedChange += (_, _) => Mode();
        Mode();

        Buttons(card, () =>
        {
            step.KeyMode = type.Checked ? "type" : down.Checked ? "down" : up.Checked ? "up" : "press";
            step.Text = (text.Text ?? "")[..Math.Min((text.Text ?? "").Length, MacroLimits.TypeTextMaxLength)];
            var name = KeyCatalog.TryGetVirtualKey(key.Text ?? "", out var vk) ? KeyCatalog.NameOf(vk) ?? key.Text!.Trim() : null;
            if (step.KeyMode != "type" && name is null)
            {
                Ui.Toast(C, $"Night-loop doesn't know the key \"{key.Text}\". Try A, 5, Enter, Space or Up.");
                return false;
            }
            if (step.KeyMode == "type" && step.Text.Length == 0)
            {
                Ui.Toast(C, "Write the text to type.");
                return false;
            }
            if (name is not null) step.Key = name;
            step.KeyHoldMs = Math.Clamp(Int(hold, 50), 0, MacroLimits.WaitMaxMs);
            return true;
        }, step);
        ShowCard(card, step.Layout.X + Px(40), step.Layout.Y);
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
            if (newName.Length > MacroLimits.NameMaxLength) newName = newName[..MacroLimits.NameMaxLength];
            // Renamed through the draft like every other change (a separate rename was undone by the next autosave).
            if (newName.Length > 0 && newName != _name)
            {
                _name = newName;
                _doc.Name = newName;
                if (_title is not null) _title.Text = $"✥ {newName}";
            }
            CloseCard();
            Changed();
        }));
        buttons.AddView(Ui.Button(C, "Cancel", CloseCard, primary: false));
        card.AddView(buttons);
        ShowCard(card, _toolbarLayout?.X ?? 0, (_toolbarLayout?.Y ?? 0) + Px(120));
    }

    private void Buttons(LinearLayout card, Action apply, Step step) => Buttons(card, () =>
    {
        apply();
        return true;
    }, step);

    private void Buttons(LinearLayout card, Func<bool> apply, Step step)
    {
        var row = Ui.Row(C);
        row.AddView(Ui.Button(C, "Done", () =>
        {
            if (!apply()) return;
            CloseCard();
            PaintAll();
            Changed();
        }));
        row.AddView(Ui.Button(C, "Delete", () =>
        {
            _selected = step;
            RemoveSelected();
        }, primary: false));
        card.AddView(row);
    }

    private static int Int(EditText input, int fallback) => int.TryParse(input.Text, out var v) ? v : fallback;

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
        var scroll = new ScrollView(C);
        scroll.AddView(card);
        var layout = new WindowManagerLayoutParams(width, ViewGroup.LayoutParams.WrapContent, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotTouchModal | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Math.Clamp(x, Px(8), Math.Max(Px(8), metrics.WidthPixels - width - Px(8))),
            Y = Math.Clamp(y, Px(8), Math.Max(Px(8), metrics.HeightPixels - Px(420))),
            SoftInputMode = SoftInput.AdjustPan | SoftInput.StateHidden,
        };
        _windows.AddView(scroll, layout);
        _card = scroll;
    }

    private void CloseCard()
    {
        if (_card is not null) _windows.RemoveView(_card);
        _card = null;
    }

    // ---- Saving ----

    private void Changed()
    {
        _doc.Blocks = ToBlocks();
        _saver.Changed(_doc);
        ShowStatus();
    }

    /// <summary>Steps → blocks, keeping their ids so saving doesn't count them as new blocks.</summary>
    private List<Block> ToBlocks()
    {
        var blocks = new List<Block>();
        _byBlockId.Clear();
        foreach (var step in _steps)
        {
            switch (step.Kind)
            {
                case Kind.Touch:
                    var (x, y) = Center(step);
                    var p = new JsonObject { ["x"] = x, ["y"] = y, ["holdMs"] = step.PressMs };
                    if (!step.Hold && step.Count > 1) p["count"] = step.Count;
                    Add(Block.Create(step.Id, step.Hold ? BlockTypes.LongPress : BlockTypes.Tap, p), step);
                    if (step.WaitMs > 0)
                    {
                        step.WaitId ??= AfterPrefix + step.Id;
                        Add(Block.Create(step.WaitId, BlockTypes.Wait, new() { ["ms"] = step.WaitMs }), step);
                    }
                    break;
                case Kind.Wait:
                    Add(Block.Create(step.Id, BlockTypes.Wait, new() { ["ms"] = step.Ms }), step);
                    break;
                default:
                    Add(step.KeyMode switch
                    {
                        "type" => Block.Create(step.Id, BlockTypes.Type, new() { ["text"] = step.Text }),
                        "press" => Block.Create(step.Id, BlockTypes.Key, new() { ["key"] = step.Key, ["action"] = "press", ["holdMs"] = step.KeyHoldMs }),
                        _ => Block.Create(step.Id, BlockTypes.Key, new() { ["key"] = step.Key, ["action"] = step.KeyMode }),
                    }, step);
                    break;
            }
        }
        return blocks;

        void Add(Block block, Step step)
        {
            blocks.Add(block);
            _byBlockId[block.Id] = step;
        }
    }

    /// <summary>
    /// The dot's middle in screen pixels, where the tap lands. Dot windows are laid out in screen coordinates
    /// (LayoutInScreen + LayoutNoLimits, into the cutout), so the window position is the screen position.
    /// </summary>
    private (int X, int Y) Center(Step dot)
    {
        var half = Px(DotDp) / 2;
        return (Math.Max(0, dot.Layout.X + half), Math.Max(0, dot.Layout.Y + half));
    }

    private string NextId()
    {
        var used = new HashSet<string>(_steps.SelectMany(s => new[] { s.Id, s.WaitId }).OfType<string>(), StringComparer.Ordinal);
        for (var i = 1; ; i++)
            if (!used.Contains($"b{i}")) return $"b{i}";
    }

    // ---- Playing ----

    /// <summary>▶ plays with the steps still on screen (see-through, taps pass through); ■ stops.</summary>
    public async Task TogglePlayAsync()
    {
        if (_isPlaying)
        {
            Night.Stop(_id);
            return;
        }
        CloseCard();
        if (_steps.Count == 0)
        {
            Ui.Toast(C, "Tap + to add a dot first.");
            return;
        }
        Changed();
        var copy = MacroJson.Deserialize(MacroJson.Serialize(_doc));
        if (await Night.PlayAsync(_id, copy) is { } error)
        {
            Ui.Toast(C, error);
            return;
        }
        if (Night.RunOf(_id) is not { } run) return;
        SetPlaying(true);
        run.StepChanged += OnStep;
        try { await run.Completion; }
        catch (Exception) { /* Night shows faults */ }
        run.StepChanged -= OnStep;
        Night.OnMain(() =>
        {
            if (!_closed) SetPlaying(false);
        });

        void OnStep(MacroRun r, int index) => Night.OnMain(() =>
        {
            var block = index >= 0 && index < r.Macro.Steps.Length ? r.Macro.Steps[index].Block : null;
            _playing = block is not null && _byBlockId.TryGetValue(block.Id, out var step) ? step : null;
            PaintAll();
        });
    }

    private void OnRunsChanged()
    {
        if (_isPlaying && !Night.IsRunning(_id)) SetPlaying(false);
    }

    private void SetPlaying(bool playing)
    {
        if (_isPlaying == playing) return;
        _isPlaying = playing;
        _playing = null;
        if (playing) _selected = null;
        foreach (var step in _steps) PassThrough(step, playing);
        if (_playButton is not null) _playButton.Text = playing ? "■" : "▶";
        PaintAll();
    }

    /// <summary>While playing, steps stay visible but see-through and untouchable, so the taps reach the game.</summary>
    private void PassThrough(Step step, bool on)
    {
        if (on) step.Layout.Flags |= WindowManagerFlags.NotTouchable;
        else step.Layout.Flags &= ~WindowManagerFlags.NotTouchable;
        step.Layout.Alpha = on ? PlayingAlpha : 1f;
        if (step.View.IsAttachedToWindow) _windows.UpdateViewLayout(step.View, step.Layout);
    }

    // ---- Closing ----

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
        Night.RunsChanged -= OnRunsChanged;
        CloseCard();
        foreach (var step in _steps)
            if (step.View.IsAttachedToWindow) _windows.RemoveView(step.View);
        if (_toolbar is not null) _windows.RemoveView(_toolbar);
        _toolbar = null;
        _ = _saver.DisposeAsync().AsTask();
        Closed?.Invoke();
    }

    /// <summary>Drags a step (or the toolbar). A touch that hardly moves is a tap: it opens the step's settings.</summary>
    private sealed class Drag(DotEditor editor, WindowManagerLayoutParams layout, Step? step) : Java.Lang.Object, View.IOnTouchListener
    {
        private float _downX, _downY;
        private int _startX, _startY;
        private bool _moved;

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (v is null || e is null) return false;
            var target = step?.View ?? (View?)editor._toolbar;
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
                    if (step is null) return true;
                    if (_moved)
                    {
                        if (step.Kind == Kind.Touch) editor.Changed(); // a chip's place isn't saved
                    }
                    else editor.Select(step);
                    return true;
            }
            return false;
        }
    }
}
