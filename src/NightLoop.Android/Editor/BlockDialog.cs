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
        {
            foreach (var field in kind.Fields)
                AddField(activity, body, copy, field, apply, isNew, done);
            if (NightLoop.Engine.Blocks.DetectProbe.CanTest(copy)) AddTest(activity, body, copy, apply, isNew, done);
        }

        var builder = Dialogs.Builder(context)
            .SetTitle(kind?.Name ?? block.Type)!
            .SetView(Ui.Scroll(activity, body))!
            .SetPositiveButton(isNew ? "Add" : "OK", (_, _) =>
            {
                LastTest = null;
                foreach (var a in apply) a();
                done(copy);
            })!
            .SetNegativeButton("Cancel", (_, _) => LastTest = null)!;
        Current = Dialogs.Show(context, builder);
        // Closed with Back or a touch outside: an old test result isn't shown the next time.
        Current.CancelEvent += (_, _) => LastTest = null;
    }

    /// <summary>
    /// What the last "Test on screen now" said, for which block and with which settings. The window closes for the
    /// look and shows this when it opens again; once the block's settings change (a new picture, say) it no longer applies.
    /// </summary>
    private static (string BlockId, string Settings, string Message)? LastTest { get; set; }

    /// <summary>
    /// "Test on screen now" on the steps that look at the screen: one look, right now, the way the macro looks while it
    /// plays, and what it saw (for a picture: how alike the best spot is, and whether its colors matched). Nothing is
    /// tapped. It answers "why did it tap that?" on the phone itself.
    /// </summary>
    private static void AddTest(Context a, LinearLayout body, Block copy, List<Action> apply, bool isNew, Action<Block> done)
    {
        Label(a, body, "Test");
        var said = LastTest is { } last && last.BlockId == copy.Id && last.Settings == copy.Params.ToJsonString();
        body.AddView(Ui.Text(a, said ? LastTest!.Value.Message : "Looks at the screen once, right now, and says what it sees. Nothing is tapped.",
            14, said ? Palette.Text : Palette.TextQuiet));
        body.AddView(Ui.Button(a, "Test on screen now", () =>
        {
            foreach (var w in apply) w();
            Current?.Dismiss();
            TestOnScreen(a, copy, () => Show(a, copy, isNew, done));
        }, primary: false));
    }

    private static void TestOnScreen(Context a, Block copy, Action back)
    {
        if (!Core.ScreenCapture.Ready)
        {
            Core.ScreenConsentActivity.Ask(a);
            Ui.Toast(a, "Tap \"Start now\" so Night-loop can see the screen, then test again.");
            back();
            return;
        }
        // From the app's own editor, step aside first so the look sees the app underneath, as picking does. Night-loop's
        // dots, chips and bubble hide for the look, as they do for a pick.
        var activity = a as Activity;
        activity?.MoveTaskToBack(true);
        var hidden = Core.Overlays.HideAll();
        Night.MainHandler.PostDelayed(() =>
        {
            Task.Run(() => NightLoop.Engine.Blocks.DetectProbe.LookOnceAsync(copy, Night.LookEnvironment(), null))
                .ContinueWith(look => Night.OnMain(() =>
                {
                    Core.Overlays.ShowAgain(hidden);
                    ScreenPicker.ComeBack(a);
                    var message = look.IsCompletedSuccessfully
                        ? look.Result.Message.Replace("use \"Capture image from screen\" on this block", "use \"Pick picture on screen\" on this step")
                        : $"Couldn't look at the screen: {look.Exception?.GetBaseException().Message}";
                    LastTest = (copy.Id, copy.Params.ToJsonString(), message);
                    back();
                }));
        }, activity is null ? 400 : 1000);
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
                // "Max tries" and the like take the word Infinite too.
                var infinite = f.Hint == "Infinite";
                var brightness = f.Key == BlockTypes.BrightnessKey;
                var shown = brightness ? (int.TryParse(Brightness.Shown(p), out var bp) ? bp : (int?)null) : copy.GetInt(f.Key);
                var input = infinite ? Ui.NumberOrInfinite(a, f.Hint, shown) : Ui.Number(a, f.Hint, shown);
                body.AddView(input);
                apply.Add(() =>
                {
                    if (brightness)
                    {
                        Brightness.Write(p, int.TryParse((input.Text ?? "").Trim(), out var b) ? b : null);
                        return;
                    }
                    if (int.TryParse((input.Text ?? "").Trim(), out var v)) p[f.Key] = v;
                    else if (!f.Required) p.Remove(f.Key);
                });
                break;
            }
            case TimeoutField f:
            {
                // Milliseconds, like the PC app. Blank stores 0 (Infinite) when the field allows it.
                var stored = copy.GetInt(f.Key);
                // Typing the word Infinite works as well as leaving it blank.
                var input = Ui.NumberOrInfinite(a, f.Hint, stored is > 0 ? stored : null);
                body.AddView(input);
                apply.Add(() =>
                {
                    var text = (input.Text ?? "").Trim();
                    if (int.TryParse(text, out var ms) && ms >= 0) p[f.Key] = Math.Min(MacroLimits.WaitMaxMs, ms);
                    else if (f.BlankIsInfinite || (text.Length > 0 && Ui.SaysInfinite(text))) p[f.Key] = 0;
                    else p.Remove(f.Key);
                });
                break;
            }
            case LoopCountField:
            {
                var input = Ui.NumberOrInfinite(a, "Infinite", copy.GetBool("forever") == true ? null : copy.GetInt("count"));
                body.AddView(input);
                apply.Add(() =>
                {
                    if (int.TryParse((input.Text ?? "").Trim(), out var v) && v > 0)
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
                    var spots = copy.Type switch
                    {
                        BlockTypes.WalkTo => new[] { "Touch the middle of the joystick", "Touch where the joystick is pushed to walk forward" },
                        BlockTypes.Swipe or BlockTypes.SwipeHold or BlockTypes.SwipeScan => new[] { "Touch where the swipe starts", "Touch where the swipe ends" },
                        _ => new[] { "Touch the spot to tap" },
                    };
                    ScreenPicker.Pick((Activity)a, spots, picked =>
                    {
                        if (copy.Type is BlockTypes.Swipe or BlockTypes.SwipeHold or BlockTypes.SwipeScan or BlockTypes.WalkTo && picked.Count == 2)
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
                apply.Add(() =>
                {
                    if (f.Key == "failLabel" && string.IsNullOrWhiteSpace(input.Text)) p.Remove(f.Key);
                    else p[f.Key] = input.Text ?? "";
                });
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
            case PictureField:
            {
                var preview = Ui.Text(a, copy.GetString("imageData") is { Length: > 0 } ? "Picture saved. Pick again to change it." : "No picture yet.", 14, Palette.TextQuiet);
                body.AddView(preview);
                if (Preview(a, copy.GetString("imageData")) is { } shown) body.AddView(shown);
                body.AddView(Ui.Button(a, "Pick picture on screen", () =>
                {
                    foreach (var w in apply) w();
                    Current?.Dismiss();
                    PickPicture(a, data =>
                    {
                        if (data is not null) p["imageData"] = data;
                        Show(a, copy, isNew, done);
                    });
                }, primary: false));
                break;
            }
            case TapSpotField:
            {
                var (sx, sy) = ClickSpot.Read(copy);
                body.AddView(Ui.Text(a, ClickSpot.Describe(sx, sy, "Taps") + ".", 14, Palette.Text));
                var row = Ui.Row(a);
                row.AddView(Ui.Fill(Ui.Button(a, "Pick tap spot on screen", () =>
                {
                    foreach (var w in apply) w();
                    if (copy.GetString("imageData") is not { Length: > 0 })
                    {
                        Ui.Toast(a, "Pick the picture first.");
                        return;
                    }
                    Current?.Dismiss();
                    PickTapSpot(a, copy, () => Show(a, copy, isNew, done));
                }, primary: false)));
                row.AddView(Ui.Button(a, "Middle", () =>
                {
                    foreach (var w in apply) w();
                    ClickSpot.Write(copy, 0.5, 0.5);
                    Current?.Dismiss();
                    Show(a, copy, isNew, done);
                }, primary: false));
                body.AddView(row);
                break;
            }
            case PixelField:
            {
                body.AddView(Ui.Text(a, $"{copy.GetInt("x", 0)}, {copy.GetInt("y", 0)} · {copy.GetString("color", "#FFFFFF")}", 14, Palette.Text));
                body.AddView(Ui.Button(a, "Pick spot and color on screen", () =>
                {
                    foreach (var w in apply) w();
                    Current?.Dismiss();
                    PickPixel(a, picked =>
                    {
                        if (picked is { } px)
                        {
                            p["x"] = px.X;
                            p["y"] = px.Y;
                            p["color"] = px.Color;
                        }
                        Show(a, copy, isNew, done);
                    });
                }, primary: false));
                break;
            }
            case ConditionField cf:
                AddCondition(a, body, copy, apply, isNew, done, cf.Optional);
                break;
        }
    }

    private static readonly (string Kind, string Text)[] CheckKinds =
    [
        ("appInFront", "App is open (in front)"), ("!appInFront", "App is not open (not in front)"),
        ("textVisible", "Text is on screen"), ("!textVisible", "Text is not on screen"),
        ("imageVisible", "Picture is on screen"), ("!imageVisible", "Picture is not on screen"),
        ("pixelColor", "Spot is a color"), ("!pixelColor", "Spot is not that color"),
    ];

    private static void AddCondition(Context a, LinearLayout body, Block copy, List<Action> apply, bool isNew, Action<Block> done, bool optional = false)
    {
        var p = copy.Params;
        var c = copy.GetObject("condition");
        var negated = BlockKinds.Str(c?["kind"]) == "not";
        var inner = (negated ? c!["condition"] as JsonObject : c)?.DeepClone() as JsonObject ?? new JsonObject { ["kind"] = "appInFront", ["package"] = "" };
        var kind = BlockKinds.Str(inner["kind"]) ?? "appInFront";
        var none = optional && c is null;
        if (CheckKinds.All(k => k.Kind != kind))
        {
            body.AddView(Ui.Text(a, "This check was made in another version and can't be changed here.", 14, Palette.TextQuiet));
            return;
        }
        var group = new RadioGroup(a) { Orientation = Orientation.Vertical };
        var ids = new Dictionary<int, string>();
        var choices = optional ? CheckKinds.Prepend(("none", "No check: hold for the hold time")) : CheckKinds;
        foreach (var (value, text) in choices)
        {
            var radio = new RadioButton(a) { Text = text, Id = global::Android.Views.View.GenerateViewId() };
            radio.SetTextColor(Palette.Text);
            ids[radio.Id] = value;
            group.AddView(radio);
            if (value == (none ? "none" : negated ? "!" + kind : kind)) radio.Checked = true;
        }
        body.AddView(group);
        var details = Ui.Column(a, 0);
        body.AddView(details);

        Func<JsonObject> read = () => inner;
        void Fill(string k)
        {
            details.RemoveAllViews();
            if (k == "none") return;
            if (BlockKinds.Str(inner["kind"]) != k) inner = new JsonObject { ["kind"] = k };
            switch (k)
            {
                case "appInFront":
                {
                    var package = BlockKinds.Str(inner["package"]) ?? "";
                    var button = Ui.Button(a, BlockKinds.AppName(package), () => { }, primary: false);
                    button.Click += (_, _) => AppPicker.Pick(a, picked =>
                    {
                        package = picked;
                        button.Text = BlockKinds.AppName(picked);
                    });
                    details.AddView(button);
                    read = () => new JsonObject { ["kind"] = "appInFront", ["package"] = package };
                    break;
                }
                case "textVisible":
                {
                    var text = Ui.Input(a, "Text to look for", BlockKinds.Str(inner["text"]) ?? "");
                    var exact = new CheckBox(a) { Text = "Exact words only", Checked = BlockKinds.Str(inner["match"]) == "exact" };
                    exact.SetTextColor(Palette.Text);
                    details.AddView(text);
                    details.AddView(exact);
                    read = () => new JsonObject { ["kind"] = "textVisible", ["text"] = text.Text ?? "", ["match"] = exact.Checked ? "exact" : "contains" };
                    break;
                }
                case "imageVisible":
                {
                    var has = BlockKinds.Str(inner["imageData"]) is { Length: > 0 };
                    details.AddView(Ui.Text(a, has ? "Picture saved." : "No picture yet.", 14, Palette.TextQuiet));
                    if (Preview(a, BlockKinds.Str(inner["imageData"])) is { } shown) details.AddView(shown);
                    var conf = Ui.Number(a, "Confidence % (90)", (inner["confidencePct"] as JsonValue)?.TryGetValue(out int cp) == true ? cp : null);
                    var size = Ui.Number(a, "Size tolerance % (blank = automatic)", (inner["sizeTolerancePct"] as JsonValue)?.TryGetValue(out int sp) == true ? sp : null);
                    var color = new CheckBox(a) { Text = "Match colors", Checked = !((inner["matchColor"] as JsonValue)?.TryGetValue(out bool mc) == true && !mc) };
                    color.SetTextColor(Palette.Text);
                    var average = new CheckBox(a) { Text = "Average colors (any dimmer or brighter copy)", Checked = (inner[BlockTypes.AverageColorsKey] as JsonValue)?.TryGetValue(out bool av) == true && av };
                    average.SetTextColor(Palette.Text);
                    var dimmer = Ui.Number(a, $"Brightness range % when not averaging ({Brightness.DefaultPct})", int.TryParse(Brightness.Shown(inner), out var bp) ? bp : null);
                    details.AddView(conf);
                    details.AddView(size);
                    details.AddView(color);
                    details.AddView(average);
                    details.AddView(dimmer);
                    read = () =>
                    {
                        var o = new JsonObject { ["kind"] = "imageVisible" };
                        if (BlockKinds.Str(inner["imageData"]) is { } d) o["imageData"] = d;
                        if (int.TryParse(conf.Text, out var cv)) o["confidencePct"] = cv;
                        if (int.TryParse(size.Text, out var sv)) o["sizeTolerancePct"] = sv;
                        if (!color.Checked) o["matchColor"] = false;
                        if (average.Checked) o[BlockTypes.AverageColorsKey] = true;
                        Brightness.Write(o, int.TryParse((dimmer.Text ?? "").Trim(), out var bv) ? bv : null);
                        return o;
                    };
                    details.AddView(Ui.Button(a, "Pick picture on screen", () =>
                    {
                        Save();
                        Current?.Dismiss();
                        PickPicture(a, data =>
                        {
                            if (data is not null && copy.GetObject("condition") is { } saved)
                            {
                                var target = BlockKinds.Str(saved["kind"]) == "not" ? saved["condition"] as JsonObject : saved;
                                if (target is not null) target["imageData"] = data;
                            }
                            Show(a, copy, isNew, done);
                        });
                    }, primary: false));
                    break;
                }
                case "pixelColor":
                {
                    var x = (inner["x"] as JsonValue)?.TryGetValue(out int xv) == true ? xv : 0;
                    var y = (inner["y"] as JsonValue)?.TryGetValue(out int yv) == true ? yv : 0;
                    var col = BlockKinds.Str(inner["color"]) ?? "#FFFFFF";
                    var label = Ui.Text(a, $"{x}, {y} · {col}", 14, Palette.Text);
                    var tol = Ui.Number(a, "Color tolerance (10)", (inner["tolerance"] as JsonValue)?.TryGetValue(out int tv) == true ? tv : null);
                    details.AddView(label);
                    details.AddView(tol);
                    read = () =>
                    {
                        var o = new JsonObject { ["kind"] = "pixelColor", ["x"] = x, ["y"] = y, ["color"] = col };
                        if (int.TryParse(tol.Text, out var t)) o["tolerance"] = t;
                        return o;
                    };
                    details.AddView(Ui.Button(a, "Pick spot and color on screen", () =>
                    {
                        Save();
                        Current?.Dismiss();
                        PickPixel(a, picked =>
                        {
                            if (picked is { } px && copy.GetObject("condition") is { } saved)
                            {
                                var target = BlockKinds.Str(saved["kind"]) == "not" ? saved["condition"] as JsonObject : saved;
                                if (target is not null)
                                {
                                    target["x"] = px.X;
                                    target["y"] = px.Y;
                                    target["color"] = px.Color;
                                }
                            }
                            Show(a, copy, isNew, done);
                        });
                    }, primary: false));
                    break;
                }
            }
        }
        void Save()
        {
            if (none)
            {
                p.Remove("condition");
                return;
            }
            var check = read();
            p["condition"] = negated ? new JsonObject { ["kind"] = "not", ["condition"] = check } : check;
        }
        Fill(none ? "none" : kind);
        group.CheckedChange += (_, e) =>
        {
            if (!ids.TryGetValue(e.CheckedId, out var k)) return;
            none = k == "none";
            negated = k.StartsWith('!');
            Fill(k.TrimStart('!'));
        };
        apply.Add(Save);
    }

    /// <summary>
    /// "Pick tap spot on screen": with the picture showing, touch where to tap (on it or beside it). The spot is saved
    /// relative to the picture, so it follows the picture wherever it is found later.
    /// </summary>
    private static void PickTapSpot(Context a, Block copy, Action back)
    {
        if (!Core.ScreenCapture.Ready)
        {
            Core.ScreenConsentActivity.Ask(a);
            Ui.Toast(a, "Tap \"Start now\" so Night-loop can see the screen, then pick the spot again.");
            back();
            return;
        }
        ScreenPicker.Pick(a, ["Touch where to tap once the picture is found (it must be on screen now)"], picked => AfterOverlaysHide(a, () =>
        {
            try
            {
                var (_, _, w, h) = Core.ScreenCapture.Instance.Desktop;
                var shot = Core.ScreenCapture.Instance.Capture(0, 0, w, h);
                var spot = NightLoop.Engine.Blocks.DetectProbe.SpotFromScreen(copy, shot, picked[0].X, picked[0].Y);
                if (spot.Ok)
                {
                    var (px, py) = ClickSpot.Write(copy, spot.X, spot.Y);
                    Ui.Toast(a, ClickSpot.Describe(px / 100.0, py / 100.0, "Taps") + " once it is found.");
                }
                else Ui.Toast(a, spot.Message);
            }
            catch (Exception ex)
            {
                Ui.Toast(a, $"Couldn't look at the screen: {ex.Message}");
            }
            back();
        }), comeBack: false);
    }

    /// <summary>Drag a box on the screen; the picture inside becomes the block's saved picture (null if cancelled).</summary>
    private static void PickPicture(Context a, Action<string?> got)
    {
        if (!Core.ScreenCapture.Ready)
        {
            Core.ScreenConsentActivity.Ask(a);
            Ui.Toast(a, "Tap \"Start now\" so Night-loop can see the screen, then pick the picture again.");
            got(null);
            return;
        }
        ScreenPicker.PickBox(a, "Drag a box around the picture", r => AfterOverlaysHide(a, () =>
        {
            try
            {
                // Boxes bigger than 200 px are shrunk to fit and remember their exact size on screen.
                var picked = NightLoop.Engine.Screen.ImageTemplate.FromBox(Core.ScreenCapture.Instance.Capture(r.Left, r.Top, r.Width, r.Height));
                if (picked.IsPlain)
                {
                    // One plain color matches any plain screen (a white loading screen passed Wait for picture).
                    Ui.Toast(a, "That area is all one plain color, so it would match any plain screen. Pick something with detail, like an icon or text.");
                    got(null);
                    return;
                }
                got(picked.Encode());
            }
            catch (Exception ex)
            {
                Ui.Toast(a, $"Couldn't take the picture: {ex.Message}");
                got(null);
            }
        }));
    }

    /// <summary>The saved picture, shown small in the edit window so it's clear what the step looks for.</summary>
    private static Android.Views.View? Preview(Context a, string? data)
    {
        if (!NightLoop.Engine.Screen.ImageTemplate.TryDecode(data, out var t) || t is null) return null;
        var colors = new int[t.Width * t.Height];
        for (var y = 0; y < t.Height; y++)
        for (var x = 0; x < t.Width; x++)
        {
            int g = t.Gray[y * t.Width + x];
            // The picture is saved as its light and dark plus a small copy of its colors (what Match colors
            // compares). Shown in gray only, it looked as if the colors weren't saved.
            var (r, gr, b) = t.Rgb is null ? (g, g, g) : Tinted(t, x, y, g);
            colors[y * t.Width + x] = unchecked((int)0xFF000000) | (r << 16) | (gr << 8) | b;
        }
        var bitmap = Android.Graphics.Bitmap.CreateBitmap(colors, t.Width, t.Height, Android.Graphics.Bitmap.Config.Argb8888!);
        var view = new Android.Widget.ImageView(a);
        view.SetImageBitmap(bitmap);
        view.SetAdjustViewBounds(true);
        view.SetScaleType(Android.Widget.ImageView.ScaleType.FitStart);
        var density = a.Resources!.DisplayMetrics!.Density;
        view.SetMaxHeight((int)(120 * density));
        view.SetPadding(0, (int)(6 * density), 0, (int)(6 * density));
        return view;
    }

    /// <summary>
    /// A pixel of the saved picture in color: its gray level, tinted with the saved color copy at that place (blended
    /// between the copy's cells, which are far bigger than a pixel).
    /// </summary>
    private static (int R, int G, int B) Tinted(NightLoop.Engine.Screen.ImageTemplate t, int x, int y, int gray)
    {
        var rgb = t.Rgb!;
        var fx = Math.Clamp((x + 0.5) * t.ColorWidth / t.Width - 0.5, 0, t.ColorWidth - 1);
        var fy = Math.Clamp((y + 0.5) * t.ColorHeight / t.Height - 0.5, 0, t.ColorHeight - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(t.ColorWidth - 1, x0 + 1), y1 = Math.Min(t.ColorHeight - 1, y0 + 1);
        double ax = fx - x0, ay = fy - y0;
        Span<double> c = stackalloc double[3];
        for (var k = 0; k < 3; k++)
        {
            double top = rgb[(y0 * t.ColorWidth + x0) * 3 + k] * (1 - ax) + rgb[(y0 * t.ColorWidth + x1) * 3 + k] * ax;
            double bottom = rgb[(y1 * t.ColorWidth + x0) * 3 + k] * (1 - ax) + rgb[(y1 * t.ColorWidth + x1) * 3 + k] * ax;
            c[k] = top * (1 - ay) + bottom * ay;
        }
        // Keep the pixel's own light and dark; add how far the color there is from gray.
        var level = (c[0] * 77 + c[1] * 150 + c[2] * 29) / 256;
        return ((int)Math.Clamp(gray + c[0] - level, 0, 255), (int)Math.Clamp(gray + c[1] - level, 0, 255), (int)Math.Clamp(gray + c[2] - level, 0, 255));
    }

    /// <summary>
    /// Runs <paramref name="take"/> once the screen shows neither the picker nor Night-loop's own windows (dots, chips,
    /// play bar), so a saved picture or color is only the game. Taken with them in it, a picture could match the
    /// step chip itself while the macro played and the wait went on at once.
    /// <para>
    /// Night-loop's own screen (the editor in the app) only comes back in front after that. It used to come back
    /// first, so a picture, color or tap spot picked from the app's editor was taken from Night-loop sliding back
    /// in, not from the game.
    /// </para>
    /// </summary>
    private static void AfterOverlaysHide(Context a, Action take)
    {
        var hidden = Core.Overlays.HideAll();
        new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!).PostDelayed(() =>
        {
            try { take(); }
            finally
            {
                Core.Overlays.ShowAgain(hidden);
                ScreenPicker.ComeBack(a);
            }
        }, 400);
    }

    /// <summary>Touch a spot; its position and current color come back (null if screen reading is off).</summary>
    private static void PickPixel(Context a, Action<(int X, int Y, string Color)?> got)
    {
        if (!Core.ScreenCapture.Ready)
        {
            Core.ScreenConsentActivity.Ask(a);
            Ui.Toast(a, "Tap \"Start now\" so Night-loop can see the screen, then pick the spot again.");
            got(null);
            return;
        }
        ScreenPicker.Pick(a, ["Touch the spot whose color to watch"], picked =>
        {
            var (x, y) = picked[0];
            AfterOverlaysHide(a, () =>
            {
                try
                {
                    var c = Core.ScreenCapture.Instance.Capture(x, y, 1, 1)[0, 0];
                    got((x, y, $"#{c.R:X2}{c.G:X2}{c.B:X2}"));
                }
                catch (Exception ex)
                {
                    Ui.Toast(a, $"Couldn't read the color: {ex.Message}");
                    got(null);
                }
            });
        }, comeBack: false);
    }
}
