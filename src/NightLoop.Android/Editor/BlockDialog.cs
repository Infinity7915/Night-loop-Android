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
                // "Max tries" and the like take the word Infinite too.
                var infinite = f.Hint == "Infinite";
                var input = infinite ? Ui.NumberOrInfinite(a, f.Hint, copy.GetInt(f.Key)) : Ui.Number(a, f.Hint, copy.GetInt(f.Key));
                body.AddView(input);
                apply.Add(() =>
                {
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
                    var conf = Ui.Number(a, "Confidence % (90)", (inner["confidencePct"] as JsonValue)?.TryGetValue(out int cp) == true ? cp : null);
                    var size = Ui.Number(a, "Size tolerance % (blank = automatic)", (inner["sizeTolerancePct"] as JsonValue)?.TryGetValue(out int sp) == true ? sp : null);
                    var color = new CheckBox(a) { Text = "Match colors", Checked = !((inner["matchColor"] as JsonValue)?.TryGetValue(out bool mc) == true && !mc) };
                    color.SetTextColor(Palette.Text);
                    var dimmer = new CheckBox(a) { Text = "Also match dimmer or brighter copies", Checked = (inner[BlockTypes.AverageColorsKey] as JsonValue)?.TryGetValue(out bool av) == true && av };
                    dimmer.SetTextColor(Palette.Text);
                    details.AddView(conf);
                    details.AddView(size);
                    details.AddView(color);
                    details.AddView(dimmer);
                    read = () =>
                    {
                        var o = new JsonObject { ["kind"] = "imageVisible" };
                        if (BlockKinds.Str(inner["imageData"]) is { } d) o["imageData"] = d;
                        if (int.TryParse(conf.Text, out var cv)) o["confidencePct"] = cv;
                        if (int.TryParse(size.Text, out var sv)) o["sizeTolerancePct"] = sv;
                        if (!color.Checked) o["matchColor"] = false;
                        if (dimmer.Checked) o[BlockTypes.AverageColorsKey] = true;
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

    /// <summary>Drag a box on the screen; the picture inside becomes the block's saved picture (null if cancelled).</summary>
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
        ScreenPicker.Pick(a, ["Touch where to tap once the picture is found (it must be on screen now)"], picked => AfterOverlaysHide(() =>
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
        }));
    }

    private static void PickPicture(Context a, Action<string?> got)
    {
        if (!Core.ScreenCapture.Ready)
        {
            Core.ScreenConsentActivity.Ask(a);
            Ui.Toast(a, "Tap \"Start now\" so Night-loop can see the screen, then pick the picture again.");
            got(null);
            return;
        }
        ScreenPicker.PickBox(a, "Drag a box around the picture", r => AfterOverlaysHide(() =>
        {
            try
            {
                var image = Shrink(Core.ScreenCapture.Instance.Capture(r.Left, r.Top, r.Width, r.Height));
                got(NightLoop.Engine.Screen.ImageTemplate.FromImage(image).Encode());
            }
            catch (Exception ex)
            {
                Ui.Toast(a, $"Couldn't take the picture: {ex.Message}");
                got(null);
            }
        }));
    }

    /// <summary>Pictures are at most 200 px a side; bigger boxes are scaled down (size tolerance finds them again).</summary>
    private static NightLoop.Engine.Screen.ScreenImage Shrink(NightLoop.Engine.Screen.ScreenImage image)
    {
        var max = NightLoop.Engine.Screen.ImageTemplate.MaxSide;
        if (image.Width <= max && image.Height <= max) return image;
        var scale = Math.Min((double)max / image.Width, (double)max / image.Height);
        var w = Math.Max(1, (int)(image.Width * scale));
        var h = Math.Max(1, (int)(image.Height * scale));
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var sx = Math.Min(image.Width - 1, (int)(x / scale));
            var sy = Math.Min(image.Height - 1, (int)(y / scale));
            Array.Copy(image.Bgra, (sy * image.Width + sx) * 4, px, (y * w + x) * 4, 4);
        }
        return new NightLoop.Engine.Screen.ScreenImage(image.Left, image.Top, w, h, px);
    }

    /// <summary>
    /// Runs <paramref name="take"/> once the screen shows neither the picker nor Night-loop's own windows (dots, chips,
    /// play bar), so a saved picture or color is only the game. Taken with them in it, a picture could match the
    /// step chip itself while the macro played and the wait went on at once.
    /// </summary>
    private static void AfterOverlaysHide(Action take)
    {
        var hidden = Core.Overlays.HideAll();
        new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!).PostDelayed(() =>
        {
            try { take(); }
            finally { Core.Overlays.ShowAgain(hidden); }
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
            AfterOverlaysHide(() =>
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
        });
    }
}
