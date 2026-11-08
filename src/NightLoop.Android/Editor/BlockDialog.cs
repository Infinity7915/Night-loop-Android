using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Text;
using Android.Widget;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Mobile.Core;
using NightLoop.Mobile.Screens;

namespace NightLoop.Mobile.Editor;

/// <summary>The window that edits one block's settings. Works on a copy; OK hands the copy back.</summary>
internal static class BlockDialog
{
    /// <summary>
    /// From the bubble (<paramref name="context"/> isn't an Activity) spots aren't typed in: the step's dots are
    /// dragged on the screen instead.
    /// </summary>
    public static void Show(Context context, Block block, bool isNew, Action<Block> done)
    {
        var activity = Dialogs.Themed(context);
        var kind = BlockKinds.For(block.Type);
        var copy = Copy(block);
        var body = Ui.Column(activity, Palette.Dp(activity, 20));
        var apply = new List<Action>();

        if (kind is null)
            body.AddView(Ui.Text(activity, "This block can't be edited on Android.", 14, Palette.TextQuiet));
        else
            foreach (var field in kind.Fields)
                AddField(activity, body, copy, field, apply, isNew, done);

        var builder = Dialogs.Builder(context)
            .SetTitle(kind?.Name ?? block.Type)!
            .SetView(Ui.Scroll(activity, body))!
            .SetPositiveButton(isNew ? "Add" : "OK", (_, _) =>
            {
                foreach (var a in apply) a();
                done(copy);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!;
        Current = Dialogs.Show(context, builder);
    }

    /// <summary>The open window, so "Pick on screen" can close it while you pick.</summary>
    private static AlertDialog? Current { get; set; }

    private static Block Copy(Block b) => new()
    {
        Id = b.Id,
        Type = b.Type,
        Enabled = b.Enabled,
        Breakpoint = b.Breakpoint,
        Params = (JsonObject)b.Params.DeepClone(),
    };

    private static void Label(Context c, LinearLayout body, string text)
    {
        var label = Ui.Text(c, text, 13, Palette.Accent, bold: true);
        label.SetPadding(0, Palette.Dp(c, 12), 0, 0);
        body.AddView(label);
    }

    private static void AddField(Context a, LinearLayout body, Block copy, Field field, List<Action> apply, bool isNew, Action<Block> done)
    {
        var p = copy.Params;
        Label(a, body, field.Label);
        switch (field)
        {
            case IntField f:
            {
                var input = Ui.Number(a, f.Hint, copy.GetInt(f.Key));
                body.AddView(input);
                apply.Add(() =>
                {
                    if (int.TryParse(input.Text, out var v)) p[f.Key] = v;
                    else if (!f.Required) p.Remove(f.Key);
                });
                break;
            }
            case LoopCountField:
            {
                var input = Ui.Number(a, "Infinite", copy.GetBool("forever") == true ? null : copy.GetInt("count"));
                body.AddView(input);
                apply.Add(() =>
                {
                    if (int.TryParse(input.Text, out var v) && v > 0)
                    {
                        p["count"] = v;
                        p.Remove("forever");
                    }
                    else
                    {
                        p["forever"] = true;
                        p.Remove("count");
                    }
                });
                break;
            }
            case SpotField when a is not Activity:
                body.AddView(Ui.Text(a, "Drag its dots on the screen to move it.", 14, Palette.TextQuiet));
                break;
            case SpotField f:
            {
                var point = f.Key.Length == 0 ? (copy.GetInt("x") is { } x && copy.GetInt("y") is { } y ? (x, y) : ((int, int)?)null) : copy.GetPoint(f.Key);
                var row = Ui.Row(a);
                var xs = Ui.Number(a, "x", point?.Item1);
                var ys = Ui.Number(a, "y", point?.Item2);
                row.AddView(Ui.Fill(xs));
                row.AddView(Ui.Fill(ys));
                body.AddView(row);
                void Write()
                {
                    if (!int.TryParse(xs.Text, out var x) || !int.TryParse(ys.Text, out var y)) return;
                    if (f.Key.Length == 0)
                    {
                        p["x"] = x;
                        p["y"] = y;
                    }
                    else p[f.Key] = new JsonObject { ["x"] = x, ["y"] = y };
                }
                apply.Add(Write);
                body.AddView(Ui.Button(a, "Pick on screen", () =>
                {
                    foreach (var w in apply) w();
                    Current?.Dismiss();
                    var spots = copy.Type == BlockTypes.Swipe
                        ? new[] { "Touch where the swipe starts", "Touch where the swipe ends" }
                        : new[] { "Touch the spot to tap" };
                    ScreenPicker.Pick((Activity)a, spots, picked =>
                    {
                        if (copy.Type == BlockTypes.Swipe && picked.Count == 2)
                        {
                            p["from"] = new JsonObject { ["x"] = picked[0].X, ["y"] = picked[0].Y };
                            p["to"] = new JsonObject { ["x"] = picked[1].X, ["y"] = picked[1].Y };
                        }
                        else
                        {
                            p["x"] = picked[0].X;
                            p["y"] = picked[0].Y;
                        }
                        Show(a, copy, isNew, done);
                    });
                }, primary: false));
                break;
            }
            case ChoiceField f:
            {
                var group = new RadioGroup(a) { Orientation = Orientation.Vertical };
                var current = copy.GetString(f.Key) ?? f.Choices[0].Value;
                var ids = new Dictionary<int, string>();
                foreach (var (value, text) in f.Choices)
                {
                    var radio = new RadioButton(a) { Text = text, Id = global::Android.Views.View.GenerateViewId() };
                    radio.SetTextColor(Palette.Text);
                    ids[radio.Id] = value;
                    group.AddView(radio);
                    if (value == current) radio.Checked = true;
                }
                body.AddView(group);
                apply.Add(() =>
                {
                    if (ids.TryGetValue(group.CheckedRadioButtonId, out var v)) p[f.Key] = v;
                });
                break;
            }
            case TextField f:
            {
                var input = Ui.Input(a, f.Label, copy.GetString(f.Key) ?? "",
                    f.Multiline ? InputTypes.ClassText | InputTypes.TextFlagMultiLine : InputTypes.ClassText);
                body.AddView(input);
                apply.Add(() => p[f.Key] = input.Text ?? "");
                break;
            }
            case BoolField f:
            {
                var box = new CheckBox(a) { Text = f.Label, Checked = copy.GetBool(f.Key) ?? f.Default };
                box.SetTextColor(Palette.Text);
                body.AddView(box);
                apply.Add(() =>
                {
                    if (box.Checked == f.Default) p.Remove(f.Key);
                    else p[f.Key] = box.Checked;
                });
                break;
            }
            case AppField f:
            {
                var package = copy.GetString(f.Key) ?? "";
                var button = Ui.Button(a, BlockKinds.AppName(package), () => { }, primary: false);
                button.Click += (_, _) => AppPicker.Pick(a, picked =>
                {
                    package = picked;
                    button.Text = BlockKinds.AppName(picked);
                });
                body.AddView(button);
                apply.Add(() => p[f.Key] = package);
                break;
            }
            case MacroField f:
            {
                var name = copy.GetString(f.Key) ?? "";
                var button = Ui.Button(a, name.Length > 0 ? name : "(pick a macro)", () => { }, primary: false);
                button.Click += (_, _) =>
                {
                    var names = Night.AndroidMacros.Select(m => m.Name).ToArray();
                    if (names.Length == 0) return;
                    Dialogs.Show(a, Dialogs.Builder(a).SetTitle(f.Label)!.SetItems(names, (_, e) =>
                    {
                        name = names[e.Which];
                        button.Text = name;
                    })!);
                };
                body.AddView(button);
                apply.Add(() => p[f.Key] = name);
                break;
            }
            case ConditionField:
            {
                var c = copy.GetObject("condition");
                var negated = BlockKinds.Str(c?["kind"]) == "not";
                var inner = negated ? c!["condition"] as JsonObject : c;
                if (inner is not null && BlockKinds.Str(inner["kind"]) is { } k && k != "appInFront")
                {
                    body.AddView(Ui.Text(a, "This check was made in another version and can't be changed here.", 14, Palette.TextQuiet));
                    break;
                }
                var package = BlockKinds.Str(inner?["package"]) ?? "";
                var button = Ui.Button(a, BlockKinds.AppName(package), () => { }, primary: false);
                button.Click += (_, _) => AppPicker.Pick(a, picked =>
                {
                    package = picked;
                    button.Text = BlockKinds.AppName(picked);
                });
                body.AddView(Ui.Text(a, "When this app is in front", 14, Palette.TextQuiet));
                body.AddView(button);
                var not = new CheckBox(a) { Text = "Check that it's NOT in front", Checked = negated };
                not.SetTextColor(Palette.Text);
                body.AddView(not);
                apply.Add(() =>
                {
                    var check = new JsonObject { ["kind"] = "appInFront", ["package"] = package };
                    p["condition"] = not.Checked ? new JsonObject { ["kind"] = "not", ["condition"] = check } : check;
                });
                break;
            }
        }
    }
}
