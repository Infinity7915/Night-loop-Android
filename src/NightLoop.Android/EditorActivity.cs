using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Mobile.Core;
using NightLoop.Mobile.Editor;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile;

/// <summary>
/// Edits one Android macro: its blocks (add, change, move, delete), how many times it repeats, and its name.
/// Changes autosave as a draft like on PC; Save makes them the copy the bubble plays.
/// </summary>
[Activity(Name = "win.nightloop.app.EditorActivity", Label = "Edit macro", Exported = false,
          WindowSoftInputMode = SoftInput.AdjustResize,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class EditorActivity : Activity
{
    public const string MacroIdExtra = "macro_id";

    private Guid _id;
    private MacroDoc _doc = new();
    private DraftAutosaver? _autosaver;
    private string _name = "";

    private TextView _title = null!;
    private TextView _sync = null!;
    private Button _play = null!;
    private EditText _repeat = null!;
    private LinearLayout _blocks = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Night.Init(this);
        if (!Guid.TryParse(Intent?.GetStringExtra(MacroIdExtra), out _id))
        {
            Finish();
            return;
        }
        var page = Ui.Column(this, Palette.Dp(this, 16));
        var top = Ui.Row(this);
        var back = Ui.Small(this, "← Back", GoHome, "Back to the home page");
        back.LayoutParameters!.Width = ViewGroup.LayoutParams.WrapContent;
        back.SetPadding(Palette.Dp(this, 12), 0, Palette.Dp(this, 12), 0);
        top.AddView(back);
        top.AddView(new Space(this), new LinearLayout.LayoutParams(Palette.Dp(this, 10), 1));
        _title = Ui.Text(this, "Loading…", 22, Palette.Text, bold: true);
        top.AddView(Ui.Fill(_title));
        top.AddView(Ui.Small(this, "✎", Rename, "Rename"));
        top.AddView(Ui.Small(this, "🗑", () => MainActivity.AskDeleteMacro(this, _id, _name.Length > 0 ? _name : "this macro", Deleted), "Delete this macro"));
        page.AddView(top);
        _sync = Ui.Text(this, "", 13, Palette.TextQuiet);
        page.AddView(_sync);

        var actions = Ui.Row(this);
        _play = Ui.Button(this, "Play", TogglePlay, primary: false);
        actions.AddView(Ui.Button(this, "Save", () => _ = SaveAsync()));
        actions.AddView(_play);
        actions.AddView(Ui.Button(this, "Add block", () => AddBlock(null), primary: false));
        page.AddView(actions);

        var repeat = Ui.Row(this);
        repeat.SetPadding(0, Palette.Dp(this, 8), 0, 0);
        repeat.AddView(Ui.Text(this, "Repeat the macro (times, blank = Infinite)  ", 14, Palette.TextQuiet));
        _repeat = Ui.NumberOrInfinite(this, "Infinite", null);
        _repeat.FocusChange += (_, e) =>
        {
            if (!e.HasFocus) ApplyRepeat();
        };
        repeat.AddView(Ui.Fill(_repeat));
        page.AddView(repeat);

        page.AddView(Ui.Heading(this, "Blocks"));
        page.AddView(Ui.Text(this, "Tap a block to change it. Hold it for more.", 13, Palette.TextQuiet));
        _blocks = Ui.Column(this, 0);
        page.AddView(_blocks);
        SetContentView(Ui.Scroll(this, page));

        Night.RunsChanged += UpdatePlay;
        _ = LoadAsync();
    }

    /// <summary>Back to the home page. The draft is flushed on pause, like the system Back.</summary>
    private void GoHome()
    {
        StartActivity(new Intent(this, typeof(MainActivity)).AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop));
        Finish();
    }

    /// <summary>The macro is gone: drop unsaved edits (nothing to save them to) and go home.</summary>
    private void Deleted()
    {
        if (_autosaver is { } saver)
        {
            _autosaver = null;
            _ = saver.DisposeAsync().AsTask();
        }
        GoHome();
    }

        protected override void OnPause()
    {
        ApplyRepeat();
        _ = FlushAsync();
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        Night.RunsChanged -= UpdatePlay;
        if (_autosaver is { } saver) _ = FinishAsync(saver);
        base.OnDestroy();
    }

    private static async Task FinishAsync(DraftAutosaver saver)
    {
        try { await saver.FlushAsync(); }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException) { }
        await saver.DisposeAsync();
    }

    private async Task FlushAsync()
    {
        try { if (_autosaver is { } saver) await saver.FlushAsync(); }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException) { }
    }

    private async Task LoadAsync()
    {
        try
        {
            var macro = await Night.Api.GetMacroAsync(_id);
            AppPicker.Installed(this); // app names for the block list
            RunOnUiThread(() =>
            {
                if (macro.Platform != MacroPlatforms.Android)
                {
                    Ui.Toast(this, "PC macros are edited in Night-loop on your PC.");
                    Finish();
                    return;
                }
                _name = macro.Name;
                _doc = MacroJson.Deserialize(MacroJson.Serialize(macro.Current ?? new MacroDoc { Name = macro.Name }));
                _doc.Platform = MacroPlatforms.Android;
                _autosaver = new DraftAutosaver(Night.Api, macro);
                var saver = _autosaver;
                _autosaver.StateChanged += state =>
                {
                    Core.Saving.KeepMineOnConflict(saver, state);
                    RunOnUiThread(ShowSync);
                };
                _title.Text = _name;
                _repeat.Text = _doc.Options.Repeat.Mode == RepeatModes.Forever ? "" : (_doc.Options.Repeat.Count ?? 1).ToString();
                ShowSync();
                UpdatePlay();
                FillBlocks();
            });
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() =>
            {
                Ui.Toast(this, ex.Message);
                Finish();
            });
        }
    }

    // ---- Sync and save ----

    private void ShowSync()
    {
        if (_autosaver is null) return;
        (_sync.Text, var color) = _autosaver.State switch
        {
            SyncState.Saved => ("Saved", Palette.Good),
            SyncState.Pending or SyncState.Saving => ("Saving draft…", Palette.TextQuiet),
            SyncState.Draft => ("Draft · autosaved. Tap Save so the bubble plays these changes.", Palette.Warn),
            SyncState.Offline => ("Offline. Changes go up when the server is back.", Palette.Warn),
            SyncState.Conflict => ("Changed somewhere else. Close and open the macro again.", Palette.Bad),
            _ => (_autosaver.Message ?? "The server refused this change.", Palette.Bad),
        };
        _sync.SetTextColor(color);
    }

    private void Changed()
    {
        _autosaver?.Changed(_doc);
        FillBlocks();
    }

    private async Task SaveAsync()
    {
        if (_autosaver is null) return;
        ApplyRepeat();
        var issues = MacroValidator.Validate(_doc);
        if (issues.Count > 0)
        {
            Ui.Toast(this, $"Fix this first: {issues[0].Message}");
            return;
        }
        try
        {
            await Core.Saving.SaveAsync(_autosaver, _doc);
            RunOnUiThread(() =>
            {
                ShowSync();
                Ui.Toast(this, "Saved.");
            });
            _ = Night.RefreshMacrosAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            RunOnUiThread(() => Ui.Toast(this, ex.Message));
        }
    }

    private void ApplyRepeat()
    {
        if (_autosaver is null) return;
        var repeat = int.TryParse((_repeat.Text ?? "").Trim(), out var n) && n > 0
            ? new RepeatOptions { Mode = RepeatModes.Count, Count = Math.Min(n, MacroLimits.CountMax) }
            : new RepeatOptions { Mode = RepeatModes.Forever, Count = null };
        var old = _doc.Options.Repeat;
        if (old.Mode == repeat.Mode && old.Count == repeat.Count) return;
        _doc.Options.Repeat = repeat;
        _autosaver.Changed(_doc);
    }

    private void Rename()
    {
        var input = Ui.Input(this, "Macro name", _name);
        var box = Ui.Column(this, Palette.Dp(this, 20));
        box.AddView(input);
        Dialogs.Builder(this).SetTitle("Rename")!.SetView(box)!
            .SetPositiveButton("OK", (_, _) =>
            {
                var name = input.Text?.Trim() ?? "";
                if (name.Length > 0 && name != _name) _ = RenameAsync(name);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!.Show();
    }

    /// <summary>
    /// Renames through the draft, like every other change. (Renaming on the side bumped the macro's version, and
    /// the next autosave put the old name back.) Save makes the name stick on the saved copy too.
    /// </summary>
    private Task RenameAsync(string name)
    {
        _name = name;
        _doc.Name = name;
        _title.Text = name;
        Changed();
        return Task.CompletedTask;
    }

    // ---- Play ----

    private void TogglePlay()
    {
        if (Night.IsRunning(_id))
        {
            Night.Stop(_id);
            return;
        }
        ApplyRepeat();
        var copy = MacroJson.Deserialize(MacroJson.Serialize(_doc));
        _ = PlayAsync(copy);
    }

    private async Task PlayAsync(MacroDoc copy)
    {
        // Step aside first; the macro starts a moment later, once the game is in front.
        MoveTaskToBack(true);
        if (await Night.PlayAsync(_id, copy) is { } error)
        {
            Ui.Toast(this, error);
            return;
        }
        Ui.Toast(this, BubbleService.Running ? "Playing. Tap the bubble to stop." : "Playing. Come back here to stop it.");
    }

    private void UpdatePlay() => _play.Text = Night.IsRunning(_id) ? "Stop" : "Play";

    // ---- Blocks ----

    private void FillBlocks()
    {
        _blocks.RemoveAllViews();
        var issues = MacroValidator.Validate(_doc).GroupBy(i => i.BlockId ?? "").ToDictionary(g => g.Key, g => g.First().Message);
        if (issues.TryGetValue("", out var general))
            _blocks.AddView(Ui.Text(this, general, 13, Palette.Bad));
        if (_doc.Blocks.Count == 0)
        {
            _blocks.AddView(Ui.Text(this, "No blocks yet. Tap Add block to start, for example with a Tap.", 14, Palette.TextQuiet));
            return;
        }
        var depths = BlockKinds.Depths(_doc.Blocks);
        for (var i = 0; i < _doc.Blocks.Count; i++)
        {
            var block = _doc.Blocks[i];
            var index = i;
            var kind = BlockKinds.For(block.Type);
            var card = Ui.Card(this);
            ((LinearLayout.LayoutParams)card.LayoutParameters!).LeftMargin = Palette.Dp(this, 16 * depths[i]);
            card.SetPadding(Palette.Dp(this, 12), Palette.Dp(this, 8), Palette.Dp(this, 8), Palette.Dp(this, 8));
            var row = Ui.Row(this);
            var info = Ui.Column(this, 0);
            info.AddView(Ui.Text(this, $"{i + 1}. {kind?.Name ?? block.Type}", 12, Palette.Accent, bold: true));
            var summary = kind?.Summary(block) ?? "Made on another version";
            info.AddView(Ui.Text(this, block.Enabled ? summary : $"{summary} (off)", 15, block.Enabled ? Palette.Text : Palette.TextQuiet));
            if (issues.TryGetValue(block.Id, out var issue)) info.AddView(Ui.Text(this, issue, 13, Palette.Bad));
            row.AddView(Ui.Fill(info));
            row.AddView(Ui.Small(this, "↑", () => Move(index, -1), "Move up"));
            row.AddView(Ui.Small(this, "↓", () => Move(index, 1), "Move down"));
            card.AddView(row);
            card.Click += (_, _) => Edit(index);
            card.LongClick += (_, _) => More(index);
            _blocks.AddView(card);
        }
    }

    private void Edit(int index)
    {
        var block = _doc.Blocks[index];
        if (BlockKinds.For(block.Type) is not { Fields.Length: > 0 }) return;
        BlockDialog.Show(this, block, isNew: false, edited =>
        {
            var at = _doc.Blocks.FindIndex(b => b.Id == edited.Id);
            if (at < 0) return;
            _doc.Blocks[at] = edited;
            Changed();
        });
    }

    private void More(int index)
    {
        var block = _doc.Blocks[index];
        string[] items = ["Add a block below", "Duplicate", block.Enabled ? "Turn off (skip it)" : "Turn on", "Delete"];
        Dialogs.Builder(this).SetTitle($"Block {index + 1}")!.SetItems(items, (_, e) =>
        {
            switch (e.Which)
            {
                case 0:
                    AddBlock(index + 1);
                    break;
                case 1:
                    var copy = MacroJson.Deserialize(MacroJson.Serialize(new MacroDoc { Blocks = [block] })).Blocks[0];
                    copy.Id = NextId();
                    _doc.Blocks.Insert(index + 1, copy);
                    Changed();
                    break;
                case 2:
                    block.Enabled = !block.Enabled;
                    Changed();
                    break;
                case 3:
                    _doc.Blocks.RemoveAt(index);
                    Changed();
                    break;
            }
        })!.Show();
    }

    private void Move(int index, int by)
    {
        var to = index + by;
        if (to < 0 || to >= _doc.Blocks.Count) return;
        (_doc.Blocks[index], _doc.Blocks[to]) = (_doc.Blocks[to], _doc.Blocks[index]);
        Changed();
    }

    private void AddBlock(int? at)
    {
        var kinds = BlockKinds.All.Where(k => k.Type != BlockTypes.End).ToArray(); // End comes with If, Loop and Repeat until
        var names = kinds.Select(k => $"{k.Group} · {k.Name}").ToArray();
        Dialogs.Builder(this).SetTitle("Add block")!.SetItems(names, (_, e) =>
        {
            var kind = kinds[e.Which];
            var block = Block.Create(NextId(), kind.Type, kind.Defaults());
            void Insert(Block b)
            {
                var where = Math.Clamp(at ?? _doc.Blocks.Count, 0, _doc.Blocks.Count);
                _doc.Blocks.Insert(where, b);
                // If, Loop and Repeat until come with their End.
                if (b.Type is BlockTypes.If or BlockTypes.Loop or BlockTypes.RepeatUntil)
                    _doc.Blocks.Insert(where + 1, Block.Create(NextId(), BlockTypes.End, []));
                Changed();
            }
            if (kind.Fields.Length == 0) Insert(block);
            else BlockDialog.Show(this, block, isNew: true, Insert);
        })!.Show();
    }

    private string NextId()
    {
        var used = new HashSet<string>(_doc.Blocks.Select(b => b.Id), StringComparer.Ordinal);
        for (var i = _doc.Blocks.Count + 1; ; i++)
            if (!used.Contains($"b{i}")) return $"b{i}";
    }
}
