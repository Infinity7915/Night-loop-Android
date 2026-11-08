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
using NightLoop.Mobile.Editor;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile;

/// <summary>
/// Makes a macro right on top of the game, as numbered steps that play in order. It does everything the app's
/// editor does:
/// <list type="bullet">
/// <item>a dot (+): drag it where the tap lands; tap it for Click or Hold, how long, and the wait before the next step;</item>
/// <item>a swipe: two dots, start and end, both draggable;</item>
/// <item>a wait (⏱) and a key step (⌨);</item>
/// <item>every other block (⊕): phone buttons, apps, If / Loop / Repeat until, labels, notify, run macro…, as chips.</item>
/// </list>
/// ↑ and ↓ move the selected step earlier or later; ☰ lists every step in order. Steps stay on screen while the
/// macro plays (see-through, taps pass through them) and the playing one lights up. They are stored as ordinary
/// blocks, so the app's editor shows the same macro.
/// </summary>
internal sealed class DotEditor
{
    private const int DotDp = 46;
    private const int DefaultWaitMs = 500;
    /// <summary>Id prefix of the wait that belongs to a dot ("then wait"), so it loads back onto its dot.</summary>
    private const string AfterPrefix = "after-";
    /// <summary>Android lets taps through an app's overlay only when it is at most 80% opaque.</summary>
    private const float PlayingAlpha = 0.55f;

    private enum Kind { Touch, Swipe, Wait, Key, Other }

    /// <summary>One numbered step.</summary>
    private sealed class Step
    {
        public Kind Kind;
        public string Id = "";
        public bool Enabled = true;
        public string? Note;
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
        public int TypeDelayMs = 30; // Type text: wait between letters
        // Swipe and every other block: the block itself (a swipe's from/to follow its dots).
        public Block? Block;

        public TextView View = null!;
        public WindowManagerLayoutParams Layout = null!;
        // A swipe's end dot.
        public TextView? EndView;
        public WindowManagerLayoutParams? EndLayout;

        public string Type => Kind switch
        {
            Kind.Touch => Hold ? BlockTypes.LongPress : BlockTypes.Tap,
            Kind.Wait => BlockTypes.Wait,
            Kind.Key => KeyMode == "type" ? BlockTypes.Type : BlockTypes.Key,
            _ => Block!.Type,
        };

        public IEnumerable<(View View, WindowManagerLayoutParams Layout)> Windows()
        {
            yield return (View, Layout);
            if (EndView is not null && EndLayout is not null) yield return (EndView, EndLayout);
        }
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
    private string? _issue;
    private LinearLayout? _toolbar;
    private WindowManagerLayoutParams? _toolbarLayout;
    private TextView? _title;
    private TextView? _status;
    private Button? _playButton;
    private View? _card;
    private View? _list;
    private ScrollView? _listScroll;
    private readonly List<View> _listRows = [];
    private int? _listX, _listY;
    private bool _isPlaying;
    private bool _starting; // Play was pressed and the start delay is running
    private bool _stopAfterStart; // ■ was pressed during that delay
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
        _saver.StateChanged += state =>
        {
            Core.Saving.KeepMineOnConflict(_saver, state);
            Night.OnMain(ShowStatus);
        };
    }

    private Context C => _service;
    private int Px(float dp) => Palette.Dp(C, dp);

    /// <summary>Opens a macro's steps on the screen, or says why it can't.</summary>
    public static async Task<(DotEditor? Editor, string? Error)> OpenAsync(BubbleService service, IWindowManager windows, Guid id)
    {
        try
        {
            var macro = await Night.Api.GetMacroAsync(id);
            if (macro.Platform != MacroPlatforms.Android) return (null, "PC macros are edited in Night-loop on your PC.");
            var doc = MacroJson.Deserialize(MacroJson.Serialize(macro.Current ?? new MacroDoc { Name = macro.Name }));
            doc.Platform = MacroPlatforms.Android;
            AppPicker.Installed(service); // app names for Open app / Wait for app
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
            if (b.Type == BlockTypes.Wait && lastTouch is not null && b.Enabled && b.Id.StartsWith(AfterPrefix, StringComparison.Ordinal))
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
                BlockTypes.Type => new Step { Kind = Kind.Key, KeyMode = "type", Text = b.GetString("text", ""), TypeDelayMs = b.GetInt("delayMs", 10) },
                BlockTypes.Key => new Step
                {
                    Kind = Kind.Key,
                    KeyMode = b.GetString("action", "press"),
                    Key = b.GetString("key", "Enter"),
                    KeyHoldMs = b.GetInt("holdMs", 50),
                },
                BlockTypes.Swipe or BlockTypes.SwipeHold or BlockTypes.SwipeScan or BlockTypes.WalkTo => new Step { Kind = Kind.Swipe, Block = Copy(b) },
                // Hold key and everything else keep their block as it is.
                _ => new Step { Kind = Kind.Other, Block = Copy(b) },
            };
            step.Id = b.Id;
            step.Enabled = b.Enabled;
            step.Note = b.GetString("note");
            _steps.Add(step);
            lastTouch = step.Kind == Kind.Touch && step.Enabled ? step : null;
            AddView(step, b.GetInt("x", 0), b.GetInt("y", 0));
        }
        ShowToolbar();
        Changed(save: false);
        Night.RunsChanged += OnRunsChanged;
    }

    private static Block Copy(Block b) => new()
    {
        Id = b.Id,
        Type = b.Type,
        Enabled = b.Enabled,
        Breakpoint = b.Breakpoint,
        Params = (JsonObject)b.Params.DeepClone(),
    };

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
        _status.SetMaxLines(2);
        bar.AddView(_status);

        var add = Ui.Row(C);
        add.AddView(Ui.Small(C, "+", AddDot, "Add a tap dot"));
        add.AddView(Ui.Small(C, "⏱", AddWait, "Add a wait"));
        add.AddView(Ui.Small(C, "⌨", () => AddKey("press"), "Add a key step"));
        add.AddView(Ui.Small(C, "⊕", ShowAddMenu, "Add any block"));
        add.AddView(Ui.Small(C, "☰", ToggleList, "List the steps in order"));
        ((LinearLayout.LayoutParams)add.GetChildAt(0)!.LayoutParameters!).LeftMargin = 0;
        bar.AddView(add);

        var edit = Ui.Row(C);
        edit.SetPadding(0, Px(6), 0, 0);
        edit.AddView(Ui.Small(C, "↑", () => MoveSelected(-1), "Move the selected step earlier"));
        edit.AddView(Ui.Small(C, "↓", () => MoveSelected(1), "Move the selected step later"));
        edit.AddView(Ui.Small(C, "−", RemoveSelected, "Remove the selected step"));
        _playButton = Ui.Small(C, "▶", () => _ = TogglePlayAsync(), "Play or stop");
        edit.AddView(_playButton);
        edit.AddView(Ui.Small(C, "⚙", ShowMacroCard, "Name and repeat"));
        edit.AddView(Ui.Small(C, "✓", () => _ = CloseAsync(), "Save and close"));
        ((LinearLayout.LayoutParams)edit.GetChildAt(0)!.LayoutParameters!).LeftMargin = 0;
        bar.AddView(edit);

        var metrics = C.Resources!.DisplayMetrics!;
        _toolbarLayout = new WindowManagerLayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent,
            WindowManagerTypes.ApplicationOverlay, WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Math.Max(0, metrics.WidthPixels / 2 - Px(150)),
            Y = Px(40),
        };
        // Drag the toolbar by its title when it covers the spot you need.
        _title.SetOnTouchListener(new Drag(this, _toolbarLayout, null, end: false));
        _windows.AddView(bar, _toolbarLayout);
        Core.Overlays.Track(bar, _toolbarLayout, _windows);
        _toolbar = bar;
    }

    private void ShowStatus()
    {
        if (_status is null) return;
        if (_isPlaying)
        {
            _status.Text = _playing is null ? "Playing…" : $"Playing step {_steps.IndexOf(_playing) + 1} · ■ stops";
            _status.SetTextColor(Palette.TextQuiet);
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
        var selected = _selected is null ? "" : $" · step {_steps.IndexOf(_selected) + 1} selected";
        // The app's editor lists the wait after each tap as its own block; say so, so the two counts add up.
        var afterWaits = _steps.Count(s => s.Kind == Kind.Touch && s.WaitMs > 0);
        var count = afterWaits > 0 ? $"{_steps.Count} steps ({_steps.Count + afterWaits} blocks in the app, with waits after taps)" : $"{_steps.Count} steps";
        _status.Text = _issue is null ? $"{count} · {repeat} · {state}{selected}" : $"⚠ {_issue}";
        _status.SetTextColor(_issue is null ? Palette.TextQuiet : Palette.Warn);
    }

    // ---- Step views ----

    /// <summary>Dots sit where they tap (a swipe has a start and an end dot); everything else is a chip you can park anywhere.</summary>
    private void AddView(Step step, int centerX, int centerY)
    {
        if (step.Kind == Kind.Swipe)
        {
            var from = step.Block!.GetPoint("from") ?? (centerX, centerY);
            var to = step.Block!.GetPoint("to") ?? (from.X, from.Y - Px(150));
            (step.View, step.Layout) = MakeWindow(step, Px(DotDp), Px(DotDp), from.X - Px(DotDp) / 2, from.Y - Px(DotDp) / 2, end: false);
            var (endView, endLayout) = MakeWindow(step, Px(DotDp), Px(DotDp), to.X - Px(DotDp) / 2, to.Y - Px(DotDp) / 2, end: true);
            (step.EndView, step.EndLayout) = (endView, endLayout);
        }
        else if (step.Kind == Kind.Touch)
            (step.View, step.Layout) = MakeWindow(step, Px(DotDp), Px(DotDp), centerX - Px(DotDp) / 2, centerY - Px(DotDp) / 2, end: false);
        else
        {
            // A chip comes back where it was last left (unless Settings › Arrange steps is on). Otherwise chips line
            // up down the left side in columns, out of the way; drag them anywhere.
            var (x, y) = !Core.LocalSettings.ArrangeSteps && step.Id is { Length: > 0 } && Core.LocalSettings.ChipSpot(_id, step.Id) is { } saved
                ? ClampToScreen(saved)
                : ChipSlot(_steps.Count(s => s.Kind is not (Kind.Touch or Kind.Swipe) && s != step && s.View is not null));
            (step.View, step.Layout) = MakeWindow(step, ViewGroup.LayoutParams.WrapContent, Px(36), x, y, end: false);
            step.View.SetPadding(Px(12), 0, Px(12), 0);
            step.View.SetMaxWidth(Px(240));
            step.View.SetSingleLine(true);
            step.View.Ellipsize = TextUtils.TruncateAt.End;
        }
        foreach (var (view, layout) in step.Windows())
        {
            if (_isPlaying) PassThrough(view, layout, true);
            _windows.AddView(view, layout);
            Core.Overlays.Track(view, layout, _windows); // a tap under this dot goes to the app, not the dot
        }
    }

    /// <summary>
    /// The n-th chip's tidy spot: columns down the left side. Columns that wouldn't fit across the screen start over
    /// from the left a little lower, so every chip stays on screen (past ~30 steps they used to land off the edge).
    /// </summary>
    private (int X, int Y) ChipSlot(int n)
    {
        var metrics = C.Resources!.DisplayMetrics!;
        var perColumn = Math.Max(4, (metrics.HeightPixels - Px(230)) / Px(44));
        var columns = Math.Max(1, (metrics.WidthPixels - Px(16)) / Px(170));
        var column = n / perColumn;
        var layer = column / columns; // a full screen of columns: start a new layer, nudged down and right
        return (Px(8) + column % columns * Px(170) + layer * Px(14), Px(150) + n % perColumn * Px(44) + layer * Px(22));
    }

    private (int X, int Y) ClampToScreen((int X, int Y) p)
    {
        var metrics = C.Resources!.DisplayMetrics!;
        return (Math.Clamp(p.X, 0, Math.Max(0, metrics.WidthPixels - Px(60))), Math.Clamp(p.Y, 0, Math.Max(0, metrics.HeightPixels - Px(40))));
    }

    private (TextView, WindowManagerLayoutParams) MakeWindow(Step step, int width, int height, int x, int y, bool end)
    {
        var view = new TextView(C) { Gravity = GravityFlags.Center };
        view.SetTextColor(Color.White);
        view.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        view.SetTextSize(global::Android.Util.ComplexUnitType.Sp, step.Kind is Kind.Touch or Kind.Swipe ? 15 : 13);
        var layout = new WindowManagerLayoutParams(width, height, WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits | WindowManagerFlags.LayoutInScreen, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = x,
            Y = y,
        };
        if (OperatingSystem.IsAndroidVersionAtLeast(28))
            layout.LayoutInDisplayCutoutMode = LayoutInDisplayCutoutMode.ShortEdges;
        view.SetOnTouchListener(new Drag(this, layout, step, end));
        return (view, layout);
    }

    private void Paint(Step step)
    {
        var number = _steps.IndexOf(step) + 1;
        var off = step.Enabled ? "" : " (off)";
        step.View.Text = step.Kind switch
        {
            Kind.Touch => step.Hold ? $"{number}H" : step.Count > 1 ? $"{number}×{step.Count}" : number.ToString(),
            Kind.Swipe => $"▶{number}",
            Kind.Wait => $"{number} ⏱ {Duration(step.Ms)}{off}",
            Kind.Key => step.KeyMode switch
            {
                "type" => $"{number} ⌨ \"{Short(step.Text)}\"{off}",
                "down" => $"{number} ⌨ {step.Key} ↓{off}",
                "up" => $"{number} ⌨ {step.Key} ↑{off}",
                _ => $"{number} ⌨ {step.Key}{off}",
            },
            _ => $"{number} {Icon(step.Block!)} {Summary(step.Block!)}{off}",
        };
        // Start and end of a swipe look different: green ▶ start, red ■ end (✋ when it holds there).
        if (step.EndView is not null) step.EndView.Text = step.Block?.Type switch
        {
            BlockTypes.SwipeHold => $"✋{number}",
            BlockTypes.SwipeScan => $"🔍{number}",
            BlockTypes.WalkTo => $"🚶{number}",
            _ => $"■{number}",
        };
        var fill = step.Kind switch
        {
            Kind.Touch => Color.Argb(210, 124, 92, 246),
            Kind.Swipe => Color.Argb(210, 20, 130, 150),
            Kind.Wait => Color.Argb(220, 46, 37, 80),
            Kind.Key => Color.Argb(220, 30, 64, 120),
            _ => BlockKinds.For(step.Block!.Type)?.Group switch
            {
                "Logic" => Color.Argb(225, 88, 40, 110),
                "Apps" => Color.Argb(225, 20, 90, 80),
                "Touch" => Color.Argb(225, 60, 50, 120),
                _ => Color.Argb(225, 60, 60, 72),
            },
        };
        if (!step.Enabled) fill = Color.Argb(170, 70, 70, 80);
        var ring = step == _playing ? Palette.Good : step == _selected ? Color.White : Color.Argb(200, 237, 233, 254);
        foreach (var (view, _) in step.Windows())
        {
            var shape = new GradientDrawable();
            if (step.Kind is Kind.Touch or Kind.Swipe) shape.SetShape(ShapeType.Oval);
            else shape.SetCornerRadius(Px(18));
            var color = fill;
            if (step.Kind == Kind.Swipe && step.Enabled)
                color = view == step.EndView ? Color.Argb(215, 205, 60, 75) : Color.Argb(215, 30, 150, 80);
            shape.SetColor(step == _playing ? Color.Argb(230, 34, 160, 90) : color);
            shape.SetStroke(Px(step == _selected || step == _playing ? 3 : 2), ring);
            view.Background = shape;
        }
    }

    private void PaintAll()
    {
        foreach (var step in _steps) Paint(step);
        ShowStatus();
        if (_list is not null) FillList();
    }

    private static string Icon(Block b) => BlockKinds.For(b.Type)?.Group switch
    {
        "Touch" => "☝",
        "Apps" => "▣",
        "Timing" => "⏱",
        "Keys" => "⌨",
        "Logic" => b.Type switch
        {
            BlockTypes.Loop or BlockTypes.RepeatUntil => "↻",
            BlockTypes.End => "⌟",
            BlockTypes.Label => "⚑",
            BlockTypes.Goto => "↪",
            BlockTypes.Stop => "■",
            _ => "⑂",
        },
        _ => "✱",
    };

    private static string Summary(Block b) => BlockKinds.For(b.Type)?.Summary(b) ?? b.Type;
    private static string Duration(int ms) => ms >= 1000 && ms % 100 == 0 ? $"{ms / 1000.0:0.#} s" : $"{ms} ms";
    private static string Short(string text, int max = 12) => text.Length <= max ? text : text[..(max - 1)] + "…";

    // ---- Adding, moving and removing ----

    private void AddDot() => AddTouch(hold: false);

    private void AddTouch(bool hold)
    {
        CloseCard();
        var (x, y) = NewSpot();
        var dot = new Step
        {
            Kind = Kind.Touch,
            Id = NextId(),
            Hold = hold,
            PressMs = hold ? 600 : 50,
            WaitMs = _steps.LastOrDefault(s => s.Kind == Kind.Touch)?.WaitMs ?? DefaultWaitMs,
        };
        Insert(dot);
        AddView(dot, x, y);
        Changed();
    }

    /// <summary>New dots land near the middle, nudged so they don't sit on each other.</summary>
    private (int X, int Y) NewSpot()
    {
        var metrics = C.Resources!.DisplayMetrics!;
        var dots = _steps.Count(s => s.Kind is Kind.Touch or Kind.Swipe);
        var nudge = Px(28) * (dots % 6);
        return (metrics.WidthPixels / 2 + nudge, metrics.HeightPixels / 2 + nudge);
    }

    private void AddSwipe(Block block)
    {
        var (x, y) = NewSpot();
        { // a new swipe: put its dots near the middle
            block.Params["from"] = new JsonObject { ["x"] = x, ["y"] = y + Px(80) };
            block.Params["to"] = new JsonObject { ["x"] = x, ["y"] = y - Px(80) };
        }
        var swipe = new Step { Kind = Kind.Swipe, Id = block.Id, Block = block };
        Insert(swipe);
        AddView(swipe, x, y);
        Changed();
        Ui.Toast(C, block.Type switch
        {
            BlockTypes.SwipeHold => "Drag the dots: green ▶ is where the swipe starts, red ✋ is where it ends and keeps holding. Tap a dot to set how long to hold or what to wait for.",
            BlockTypes.SwipeScan => "Drag the dots: green ▶ is where the swipe starts, red 🔍 where it ends. Tap a dot to pick what to look for.",
            BlockTypes.WalkTo => "Put green ▶ on the middle of the joystick and red 🚶 where you push it to walk forward. Tap a dot to pick the picture to walk to.",
            _ => "Drag the dots: green ▶ is where the swipe starts, red ■ is where it ends.",
        });
    }

    private void AddWait()
    {
        CloseCard();
        var wait = new Step { Kind = Kind.Wait, Id = NextId() };
        Insert(wait);
        AddView(wait, 0, 0);
        Changed();
        ShowWaitCard(wait);
    }

    private void AddKey(string mode)
    {
        CloseCard();
        var key = new Step { Kind = Kind.Key, Id = NextId(), KeyMode = mode };
        Insert(key);
        AddView(key, 0, 0);
        PaintAll();
        ShowKeyCard(key);
    }

    private void AddBlock(Block block)
    {
        var step = new Step { Kind = Kind.Other, Id = block.Id, Block = block };
        Insert(step);
        AddView(step, 0, 0);
        // If, Loop and Repeat until come with their End, right after them; put the steps to repeat in between.
        if (block.Type is BlockTypes.If or BlockTypes.Loop or BlockTypes.RepeatUntil)
        {
            var end = new Step { Kind = Kind.Other, Id = NextId(), Block = Block.Create("", BlockTypes.End, []) };
            end.Block.Id = end.Id;
            _steps.Insert(_steps.IndexOf(step) + 1, end);
            AddView(end, 0, 0);
            _selected = step;
        }
        Changed();
    }

    /// <summary>⊕: every block the app's editor has, by group.</summary>
    private void ShowAddMenu()
    {
        CloseCard();
        CloseList();
        var kinds = BlockKinds.All.Where(k => k.Type != BlockTypes.End).ToArray(); // End comes with If, Loop and Repeat until
        var names = kinds.Select(k => $"{k.Group} · {k.Name}").ToArray();
        Dialogs.Show(C, Dialogs.Builder(C).SetTitle(_selected is null ? "Add a step at the end" : $"Add a step after {_steps.IndexOf(_selected) + 1}")!
            .SetItems(names, (_, e) => Add(kinds[e.Which]))!
            .SetNegativeButton("Cancel", (_, _) => { })!);

        void Add(BlockKind kind)
        {
            switch (kind.Type)
            {
                case BlockTypes.Tap: AddTouch(hold: false); return;
                case BlockTypes.LongPress: AddTouch(hold: true); return;
                case BlockTypes.Wait: AddWait(); return;
                case BlockTypes.Key: AddKey("press"); return;
                case BlockTypes.Type: AddKey("type"); return;
            }
            var block = Block.Create(NextId(), kind.Type, kind.Defaults());
            if (kind.Type is BlockTypes.Swipe or BlockTypes.SwipeHold or BlockTypes.SwipeScan or BlockTypes.WalkTo)
            {
                AddSwipe(block);
                return;
            }
            if (kind.Fields.Length == 0) AddBlock(block);
            else BlockDialog.Show(C, block, isNew: true, AddBlock);
        }
    }

    /// <summary>New steps go after the selected one, or at the end.</summary>
    private void Insert(Step step)
    {
        var at = _selected is null ? _steps.Count : _steps.IndexOf(_selected) + 1;
        _steps.Insert(at, step);
        _selected = step;
    }

    /// <summary>↑ / ↓: the selected step swaps places with the one before or after it.</summary>
    private void MoveSelected(int by) => Move(_selected, by);

    private void Move(Step? step, int by)
    {
        if (_isPlaying) return;
        if (step is null)
        {
            Ui.Toast(C, "Tap a step first, then ↑ or ↓ moves it earlier or later.");
            return;
        }
        CloseCard();
        var from = _steps.IndexOf(step);
        var to = from + by;
        if (from < 0 || to < 0 || to >= _steps.Count) return;
        (_steps[from], _steps[to]) = (_steps[to], _steps[from]);
        _selected = step;
        Changed();
    }

    private void RemoveSelected()
    {
        CloseCard();
        var step = _selected ?? _steps.LastOrDefault();
        if (step is null) return;
        Remove(step);
    }

    private void Remove(Step step)
    {
        CloseCard();
        foreach (var (view, _) in step.Windows())
        {
            Core.Overlays.Untrack(view);
            if (view.IsAttachedToWindow) _windows.RemoveView(view);
        }
        _steps.Remove(step);
        if (_selected == step) _selected = null;
        Changed();
    }

    private void Duplicate(Step step)
    {
        var copy = new Step
        {
            Kind = step.Kind,
            Id = NextId(),
            Enabled = step.Enabled,
            Note = step.Note,
            Hold = step.Hold,
            Count = step.Count,
            PressMs = step.PressMs,
            WaitMs = step.WaitMs,
            Ms = step.Ms,
            KeyMode = step.KeyMode,
            Key = step.Key,
            Text = step.Text,
            KeyHoldMs = step.KeyHoldMs,
            TypeDelayMs = step.TypeDelayMs,
            Block = step.Block is null ? null : Copy(step.Block),
        };
        if (copy.Block is not null)
        {
            copy.Block.Id = copy.Id;
            if (copy.Kind == Kind.Swipe) Shift(copy.Block, Px(30));
        }
        _steps.Insert(_steps.IndexOf(step) + 1, copy);
        _selected = copy;
        var (x, y) = step.Kind == Kind.Touch ? Center(step.Layout) : (0, 0);
        AddView(copy, x + Px(30), y + Px(30));
        Changed();

        static void Shift(Block b, int by)
        {
            foreach (var key in new[] { "from", "to" })
                if (b.GetPoint(key) is { } p) b.Params[key] = new JsonObject { ["x"] = p.X + by, ["y"] = p.Y + by };
        }
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
            case Kind.Key: ShowKeyCard(step); break;
            default: EditBlock(step); break;
        }
    }

    /// <summary>Swipes and other blocks use the same settings window as the app's editor.</summary>
    private void EditBlock(Step step)
    {
        CloseCard();
        var block = step.Block!;
        if (BlockKinds.For(block.Type) is not { Fields.Length: > 0 })
        {
            Ui.Toast(C, $"{Summary(block)}: nothing to set. ↑ ↓ move it, − removes it.");
            return;
        }
        BlockDialog.Show(C, block, isNew: false, edited =>
        {
            if (!_steps.Contains(step)) return;
            step.Block = edited;
            Changed();
        });
    }

    // ---- Step list ----

    /// <summary>☰: every step in order, indented inside If / Loop / Repeat until, to move, edit, copy or switch off.</summary>
    private void ToggleList()
    {
        if (_list is not null)
        {
            CloseList();
            return;
        }
        CloseCard();
        var metrics = C.Resources!.DisplayMetrics!;
        var panel = Ui.Column(C, Px(12));
        panel.Background = Ui.Rounded(C, Palette.Surface, 14, Palette.Accent);
        var top = Ui.Row(C);
        var heading = Ui.Text(C, "⠿ Steps in order", 16, Palette.Text, bold: true);
        top.AddView(Ui.Fill(heading));
        top.AddView(Ui.Small(C, "⊕", ShowAddMenu, "Add a step"));
        top.AddView(Ui.Small(C, "✕", CloseList, "Close the list"));
        panel.AddView(top);
        panel.AddView(Ui.Text(C, "Tap a step to change it. ↑ ↓ move it. Hold it to copy, switch off or delete.", 12, Palette.TextQuiet));
        var rows = Ui.Column(C, 0);
        rows.Tag = "rows";
        var scroll = new ScrollView(C);
        scroll.AddView(rows);
        _listScroll = scroll;
        panel.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var width = Math.Min(Px(380), metrics.WidthPixels - Px(16));
        var layout = new WindowManagerLayoutParams(width, Math.Min(Px(520), metrics.HeightPixels * 2 / 3), WindowManagerTypes.ApplicationOverlay,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits, Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.CenterHorizontal,
            X = _listX ?? 0,
            Y = _listY ?? (_toolbarLayout?.Y ?? 0) + Math.Max(_toolbar?.Height ?? 0, Px(150)) + Px(8), // below the toolbar so ☰ stays reachable
        };
        _windows.AddView(panel, layout);
        Core.Overlays.Track(panel, layout, _windows);
        _list = panel;
        // Drag the list by its title, like the toolbar; it reopens where you left it.
        heading.SetOnTouchListener(new Drag(this, layout, null, end: false, window: panel));
        FillList();
    }

    private void FillList()
    {
        if (_list?.FindViewWithTag("rows") is not LinearLayout rows) return;
        rows.RemoveAllViews();
        _listRows.Clear();
        if (_steps.Count == 0)
        {
            rows.AddView(Ui.Text(C, "No steps yet. + adds a tap, ⊕ any block.", 14, Palette.TextQuiet));
            return;
        }
        var depth = 0;
        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            var type = step.Type;
            if (type is BlockTypes.End or BlockTypes.Else) depth = Math.Max(0, depth - 1);
            var row = Ui.Row(C);
            row.SetPadding(Px(6 + 14 * depth), Px(4), 0, Px(4));
            PaintRow(row, step);
            var text = Ui.Text(C, $"{i + 1}. {Describe(step)}", 14, step.Enabled ? Palette.Text : Palette.TextQuiet);
            row.AddView(Ui.Fill(text));
            row.AddView(Ui.Small(C, "↑", () => Move(step, -1), "Move earlier"));
            row.AddView(Ui.Small(C, "↓", () => Move(step, 1), "Move later"));
            row.Click += (_, _) =>
            {
                if (_isPlaying) return; // the list just follows along while a macro plays
                CloseList();
                Select(step);
            };
            row.LongClick += (_, _) => More(step);
            rows.AddView(row);
            _listRows.Add(row);
            if (type is BlockTypes.If or BlockTypes.Loop or BlockTypes.RepeatUntil or BlockTypes.Else) depth++;
        }
    }

    private void PaintRow(View row, Step step)
    {
        row.Background = step == _playing && _isPlaying && LocalSettings.FollowPlayingStep
            ? Ui.Rounded(C, Color.Argb(90, 34, 160, 90), 8, Palette.Good)
            : step == _selected ? Ui.Rounded(C, Palette.SurfaceHigh, 8, Palette.Accent) : null;
    }

    /// <summary>Highlights the playing step's row in the ☰ list and scrolls it to the middle (only those rows change).</summary>
    private void FollowInList(Step? before, Step? now)
    {
        if (_list is null || !LocalSettings.FollowPlayingStep) return;
        if (before is not null && _steps.IndexOf(before) is var b and >= 0 && b < _listRows.Count) PaintRow(_listRows[b], before);
        if (now is null || _steps.IndexOf(now) is not (var i and >= 0) || i >= _listRows.Count) return;
        var row = _listRows[i];
        PaintRow(row, now);
        if (_listScroll is { } scroll)
            scroll.SmoothScrollTo(0, Math.Max(0, row.Top + row.Height / 2 - scroll.Height / 2));
    }

    private string Describe(Step step)
    {
        var text = step.Kind switch
        {
            Kind.Touch => step.Hold
                ? $"Hold {Duration(step.PressMs)} at {Center(step.Layout).X}, {Center(step.Layout).Y}"
                : $"Tap{(step.Count > 1 ? $" ×{step.Count}" : "")} at {Center(step.Layout).X}, {Center(step.Layout).Y}",
            Kind.Wait => $"Wait {Duration(step.Ms)}",
            Kind.Key => step.KeyMode switch
            {
                "type" => $"Type \"{Short(step.Text, 24)}\"",
                "down" => $"Hold {step.Key} down",
                "up" => $"Release {step.Key}",
                _ => $"Press {step.Key}",
            },
            _ => Summary(step.Block!),
        };
        if (step.Kind == Kind.Touch && step.WaitMs > 0) text += $", then wait {Duration(step.WaitMs)}";
        return step.Enabled ? text : $"{text} (off)";
    }

    private void More(Step step)
    {
        string[] items = ["Copy it", step.Enabled ? "Switch off (skip it)" : "Switch on", "Delete"];
        Dialogs.Show(C, Dialogs.Builder(C).SetTitle($"Step {_steps.IndexOf(step) + 1}")!.SetItems(items, (_, e) =>
        {
            switch (e.Which)
            {
                case 0:
                    Duplicate(step);
                    break;
                case 1:
                    step.Enabled = !step.Enabled;
                    Changed();
                    break;
                case 2:
                    Remove(step);
                    break;
            }
        })!);
    }

    private void CloseList()
    {
        Core.Overlays.Untrack(_list);
        if (_list is not null) _windows.RemoveView(_list);
        _list = null;
        _listScroll = null;
        _listRows.Clear();
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
        var speedLabel = Ui.Text(C, "Wait between letters (ms, 0 = all at once)", 13, Palette.TextQuiet);
        var speed = Ui.Number(C, "30", step.TypeDelayMs);
        foreach (var v in new View[] { textLabel, text, speedLabel, speed, keyLabel, key, holdLabel, hold }) card.AddView(v);

        void Mode()
        {
            var typing = type.Checked;
            textLabel.Visibility = text.Visibility = speedLabel.Visibility = speed.Visibility = typing ? ViewStates.Visible : ViewStates.Gone;
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
            step.TypeDelayMs = Math.Clamp(Int(speed, 30), 0, 10_000);
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
        ShowCard(card, _toolbarLayout?.X ?? 0, (_toolbarLayout?.Y ?? 0) + Px(150));
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
            Changed();
        }));
        row.AddView(Ui.Button(C, "Delete", () => Remove(step), primary: false));
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
        CloseList();
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
            SoftInputMode = SoftInput.AdjustPan | SoftInput.StateUnspecified,
        };
        _windows.AddView(scroll, layout);
        Core.Overlays.Track(scroll, layout, _windows);
        _card = scroll;
    }

    private void CloseCard()
    {
        Core.Overlays.Untrack(_card);
        if (_card is not null) _windows.RemoveView(_card);
        _card = null;
    }

    // ---- Saving ----

    private void Changed(bool save = true)
    {
        _doc.Blocks = ToBlocks();
        var issues = MacroValidator.Validate(_doc);
        _issue = issues.Count == 0 ? null
            : issues[0].BlockId is { } id && _byBlockId.TryGetValue(id, out var step) ? $"Step {_steps.IndexOf(step) + 1}: {issues[0].Message}"
            : issues[0].Message;
        if (save) _saver.Changed(_doc);
        PaintAll();
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
                    var (x, y) = Center(step.Layout);
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
                case Kind.Key:
                    Add(step.KeyMode switch
                    {
                        "type" => Block.Create(step.Id, BlockTypes.Type, new() { ["text"] = step.Text, ["delayMs"] = step.TypeDelayMs }),
                        "press" => Block.Create(step.Id, BlockTypes.Key, new() { ["key"] = step.Key, ["action"] = "press", ["holdMs"] = step.KeyHoldMs }),
                        _ => Block.Create(step.Id, BlockTypes.Key, new() { ["key"] = step.Key, ["action"] = step.KeyMode }),
                    }, step);
                    break;
                case Kind.Swipe:
                    var swipe = Copy(step.Block!);
                    var from = Center(step.Layout);
                    var to = Center(step.EndLayout!);
                    swipe.Params["from"] = new JsonObject { ["x"] = from.X, ["y"] = from.Y };
                    swipe.Params["to"] = new JsonObject { ["x"] = to.X, ["y"] = to.Y };
                    step.Block = Copy(swipe);
                    Add(swipe, step);
                    break;
                default:
                    Add(Copy(step.Block!), step);
                    break;
            }
        }
        return blocks;

        void Add(Block block, Step step)
        {
            // A dot's own "then wait" follows the dot when it's switched off.
            block.Enabled = step.Enabled;
            if (step.Note is { Length: > 0 } note && block.Id == step.Id) block.Params["note"] = note;
            blocks.Add(block);
            _byBlockId[block.Id] = step;
        }
    }

    /// <summary>
    /// A dot's middle in screen pixels, where the touch lands. Dot windows are laid out in screen coordinates
    /// (LayoutInScreen + LayoutNoLimits, into the cutout), so the window position is the screen position.
    /// </summary>
    private (int X, int Y) Center(WindowManagerLayoutParams layout)
    {
        var half = Px(DotDp) / 2;
        return (Math.Max(0, layout.X + half), Math.Max(0, layout.Y + half));
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
            if (_starting) _stopAfterStart = true;
            Night.Stop(_id);
            return;
        }
        CloseCard();
        CloseList();
        if (_steps.Count == 0)
        {
            Ui.Toast(C, "Add a step first: + adds a tap.");
            return;
        }
        Changed();
        // Close the card first: it holds the keyboard focus, and the game would spend the first tap taking it back.
        CloseCard();
        CloseList();
        var copy = MacroJson.Deserialize(MacroJson.Serialize(_doc));
        // Make the dots see-through for touches BEFORE the first tap: each dot sits right where its tap lands, so a
        // dot that is still touchable catches the first tap and the game never gets it.
        SetPlaying(true);
        _starting = true;
        _stopAfterStart = false;
        string? error;
        try { error = await Night.PlayAsync(_id, copy); }
        finally { _starting = false; }
        if (_stopAfterStart)
        {
            Night.Stop(_id);
            SetPlaying(false);
            return;
        }
        if (error is not null)
        {
            SetPlaying(false);
            Ui.Toast(C, error);
            return;
        }
        if (Night.RunOf(_id) is not { } run)
        {
            SetPlaying(false);
            return;
        }
        var latest = -1;
        var queued = 0;
        run.StepChanged += OnStep;
        try { await run.Completion; }
        catch (Exception) { /* Night shows faults */ }
        run.StepChanged -= OnStep;
        Night.OnMain(() =>
        {
            if (!_closed) SetPlaying(false);
        });

        // Only the newest step is shown: if the screen falls behind, older updates are dropped instead of
        // queueing up and flashing through many dots at once.
        void OnStep(MacroRun r, int index)
        {
            Volatile.Write(ref latest, index);
            if (Interlocked.Exchange(ref queued, 1) == 0) Night.OnMain(() => Show(r));
        }

        void Show(MacroRun r)
        {
            Volatile.Write(ref queued, 0);
            var index = Volatile.Read(ref latest);
            var block = index >= 0 && index < r.Macro.Steps.Length ? r.Macro.Steps[index].Block : null;
            var now = block is not null && _byBlockId.TryGetValue(block.Id, out var step) ? step : null;
            if (now == _playing) return;
            // Repaint only the two steps that changed: repainting every window on each step kept the screen busy
            // with many steps, and the taps (sent on the same thread) piled up and then went off all at once.
            var before = _playing;
            _playing = now;
            if (before is not null && _steps.Contains(before)) Paint(before);
            if (now is not null) Paint(now);
            FollowInList(before, now);
            ShowStatus();
        }
    }

    /// <summary>The bubble's "Hide while playing" switches changed: apply them now if this macro is playing.</summary>
    public void ApplyHideSettings()
    {
        if (!_isPlaying) return;
        _isPlaying = false;
        SetPlaying(true);
    }

    private void OnRunsChanged()
    {
        if (_isPlaying && !_starting && !Night.IsRunning(_id)) SetPlaying(false);
    }

    private void SetPlaying(bool playing)
    {
        if (_isPlaying == playing) return;
        _isPlaying = playing;
        _playing = null;
        if (playing)
        {
            _selected = null;
            // An open card holds the keyboard focus, so the game's window isn't focused and swallows the first
            // tap just to take focus back. Close it before the macro starts.
            CloseCard();
        }
        foreach (var step in _steps)
            foreach (var (view, layout) in step.Windows())
                PassThrough(view, layout, playing, LocalSettings.HideDotsWhilePlaying);
        if (_toolbar is not null && _toolbarLayout is not null)
            PassThrough(_toolbar, _toolbarLayout, playing && LocalSettings.HidePlayBarWhilePlaying, hide: true);
        if (_playButton is not null) _playButton.Text = playing ? "■" : "▶";
        PaintAll();
    }

    /// <summary>While playing, steps stay visible but see-through and untouchable, so the taps reach the game.</summary>
    private void PassThrough(View view, WindowManagerLayoutParams layout, bool on, bool hide = false)
    {
        if (on) layout.Flags |= WindowManagerFlags.NotTouchable;
        else layout.Flags &= ~WindowManagerFlags.NotTouchable;
        layout.Alpha = on ? (hide ? 0f : PlayingAlpha) : 1f;
        if (view.IsAttachedToWindow) _windows.UpdateViewLayout(view, layout);
    }

    // ---- Closing ----

    /// <summary>Saves (so the bubble's ▶ plays the new version) and takes everything off the screen.</summary>
    public async Task CloseAsync()
    {
        if (_closed) return;
        CloseCard();
        CloseList();
        Changed();
        var issues = MacroValidator.Validate(_doc);
        try
        {
            if (issues.Count == 0) await Core.Saving.SaveAsync(_saver, _doc);
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
        CloseList();
        foreach (var step in _steps)
            foreach (var (view, _) in step.Windows())
            {
                Core.Overlays.Untrack(view);
                if (view.IsAttachedToWindow) _windows.RemoveView(view);
            }
        Core.Overlays.Untrack(_toolbar);
        if (_toolbar is not null) _windows.RemoveView(_toolbar);
        _toolbar = null;
        _ = _saver.DisposeAsync().AsTask();
        Closed?.Invoke();
    }

    /// <summary>Drags a step (or the toolbar). A touch that hardly moves is a tap: it opens the step's settings.</summary>
    private sealed class Drag(DotEditor editor, WindowManagerLayoutParams layout, Step? step, bool end, View? window = null) : Java.Lang.Object, View.IOnTouchListener
    {
        private float _downX, _downY;
        private int _startX, _startY;
        private bool _moved;

        public bool OnTouch(View? v, MotionEvent? e)
        {
            if (v is null || e is null) return false;
            View? target = window ?? (step is null ? editor._toolbar : end ? step.EndView : step.View);
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
                    if (window is not null) (editor._listX, editor._listY) = (layout.X, layout.Y);
                    if (step is null) return true;
                    if (_moved)
                    {
                        if (step.Kind is Kind.Touch or Kind.Swipe) editor.Changed();
                        // A chip's place isn't part of the macro: keep it on this phone so it comes back there.
                        else if (step.Id is { Length: > 0 } id) Core.LocalSettings.SaveChipSpot(editor._id, id, layout.X, layout.Y);
                    }
                    else editor.Select(step);
                    return true;
            }
            return false;
        }
    }
}
