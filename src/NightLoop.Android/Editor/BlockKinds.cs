using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Mobile.Editor;

/// <summary>One setting on a block, shown as one control in the edit window.</summary>
internal abstract record Field(string Key, string Label);
/// <summary>A whole number. Blank removes it (the block's default), unless <see cref="Required"/>.</summary>
internal sealed record IntField(string Key, string Label, string Hint, bool Required = false) : Field(Key, Label);
/// <summary>
/// A wait time in milliseconds, like the PC app. Blank stores 0 = Infinite (or, with <see cref="BlankIsInfinite"/>
/// off, removes it so the block's default applies).
/// </summary>
internal sealed record TimeoutField(string Key, string Label, bool BlankIsInfinite = true, string Hint = "Infinite") : Field(Key, Label);
/// <summary>A spot on the screen: x and y at the top level (Key "") or inside an object (from / to).</summary>
internal sealed record SpotField(string Key, string Label) : Field(Key, Label);
internal sealed record ChoiceField(string Key, string Label, (string Value, string Text)[] Choices) : Field(Key, Label);
internal sealed record TextField(string Key, string Label, bool Multiline = false) : Field(Key, Label);
internal sealed record BoolField(string Key, string Label, bool Default) : Field(Key, Label);
/// <summary>An installed app's package name, with a picker.</summary>
internal sealed record AppField(string Key, string Label) : Field(Key, Label);
/// <summary>Another Android macro, by name.</summary>
internal sealed record MacroField(string Key, string Label) : Field(Key, Label);
/// <summary>A picture to look for: drag a box on the screen (stored as imageData).</summary>
internal sealed record PictureField() : Field("imageData", "Picture");
/// <summary>A point and its color, picked by touching the screen (x, y, color).</summary>
internal sealed record PixelField() : Field("color", "Spot and color");
/// <summary>If / Repeat until: app in front, text / picture on screen, or a pixel's color (or NOT).</summary>
/// <summary>Optional: adds a "no check" choice that removes the condition (Swipe and hold).</summary>
internal sealed record ConditionField(bool Optional = false, string? Title = null) : Field("condition", Title ?? "Check");
/// <summary>Loop count, where blank means Infinite (stored as forever: true).</summary>
internal sealed record LoopCountField() : Field("count", "Times (blank = Infinite)");

internal sealed record BlockKind(string Type, string Name, string Group, Func<JsonObject> Defaults, Field[] Fields, Func<Block, string> Summary);

/// <summary>The blocks the Android editor offers, in the order of the Add block list.</summary>
internal static class BlockKinds
{
    private static readonly (string, string)[] Buttons = [("back", "Back"), ("home", "Home"), ("recents", "Recent apps"), ("notifications", "Notifications")];

    private static readonly (string, string)[] Matches = [("exact", "Exact words"), ("contains", "Contains (part of a word counts)")];
    private static readonly (string, string)[] OnFails = [("continue", "Go on"), ("retry", "Try again"), ("goto", "Go to a label"), ("stop", "Stop the macro")];

    private static Field[] TextFields =>
    [
        new TextField("text", "Text to look for"), new ChoiceField("match", "Match", Matches),
        new BoolField("matchCase", "Match capital letters", false),
        new TimeoutField("timeoutMs", "Give up after (ms, blank = Infinite)"),
        new ChoiceField("onFail", "If it gives up", OnFails), new TextField("failLabel", "Label to go to (for Go to a label)"),
    ];

    private static readonly (string, string)[] Targets = [("image", "A picture"), ("text", "Text")];
    private static readonly (string, string)[] WhenFound = [("none", "Just go on"), ("tap", "Tap it, then go on")];
    private static Field[] Tapping(Field[] fields) => [.. fields, new ChoiceField("whenFound", "When found", WhenFound)];

    private static Field[] PictureFields =>
    [
        new PictureField(), new IntField("confidencePct", "Confidence % (1–100)", "90"),
        new IntField("sizeTolerancePct", "Size tolerance % (blank = automatic)", "auto"),
        new BoolField("matchColor", "Match colors", true),
        new TimeoutField("timeoutMs", "Give up after (ms, blank = Infinite)"),
        new ChoiceField("onFail", "If it gives up", OnFails), new TextField("failLabel", "Label to go to (for Go to a label)"),
    ];

    private static Field[] PixelFields =>
    [
        new PixelField(), new IntField("tolerance", "Color tolerance (0–255)", "10"),
        new TimeoutField("timeoutMs", "Give up after (ms, blank = Infinite)"),
        new ChoiceField("onFail", "If it gives up", OnFails), new TextField("failLabel", "Label to go to (for Go to a label)"),
    ];

    public static readonly BlockKind[] All =
    [
        new(BlockTypes.Tap, "Tap", "Touch", () => new() { ["x"] = 540, ["y"] = 1200 },
            [new SpotField("", "Spot"), new IntField("count", "Taps (1–3)", "1"), new IntField("holdMs", "Press length (ms)", "50")],
            b => $"Tap at {b.GetInt("x", 0)}, {b.GetInt("y", 0)}{(b.GetInt("count", 1) > 1 ? $" × {b.GetInt("count", 1)}" : "")}"),
        new(BlockTypes.LongPress, "Long press", "Touch", () => new() { ["x"] = 540, ["y"] = 1200, ["holdMs"] = 600 },
            [new SpotField("", "Spot"), new IntField("holdMs", "Hold (ms)", "600")],
            b => $"Long press at {b.GetInt("x", 0)}, {b.GetInt("y", 0)} for {b.GetInt("holdMs", 600)} ms"),
        new(BlockTypes.Swipe, "Swipe", "Touch",
            () => new() { ["from"] = new JsonObject { ["x"] = 540, ["y"] = 1600 }, ["to"] = new JsonObject { ["x"] = 540, ["y"] = 800 }, ["durationMs"] = 300 },
            [new SpotField("from", "Start"), new SpotField("to", "End"), new IntField("durationMs", "Swipe time (ms)", "300")],
            b => b.GetPoint("from") is { } f && b.GetPoint("to") is { } t ? $"Swipe {f.X}, {f.Y} → {t.X}, {t.Y}" : "Swipe"),
        new(BlockTypes.SwipeHold, "Swipe and hold", "Touch",
            () => new() { ["from"] = new JsonObject { ["x"] = 300, ["y"] = 1700 }, ["to"] = new JsonObject { ["x"] = 300, ["y"] = 1500 }, ["durationMs"] = 300, ["holdMs"] = 2000 },
            [new SpotField("from", "Start"), new SpotField("to", "End (the finger stays down here)"), new IntField("durationMs", "Swipe time (ms)", "300"),
             new IntField("holdMs", "Hold time (ms), when there's no check", "1000"),
             new ConditionField(Optional: true, Title: "Keep holding until"),
             new TimeoutField("timeoutMs", "With a check: let go after (ms, blank = Infinite)")],
            b => b.GetObject("condition") is { } c ? $"Swipe and hold until {DescribeCondition(c)}" : $"Swipe and hold {b.GetInt("holdMs", 1000)} ms"),
        new(BlockTypes.SwipeScan, "Swipe and scan", "Touch",
            () => new() { ["from"] = new JsonObject { ["x"] = 800, ["y"] = 900 }, ["to"] = new JsonObject { ["x"] = 500, ["y"] = 900 }, ["durationMs"] = 300,
                          ["target"] = "image", ["confidencePct"] = 90, ["maxTurns"] = 12, ["whenFound"] = "none" },
            [new SpotField("from", "Swipe start"), new SpotField("to", "Swipe end"), new IntField("durationMs", "Swipe time (ms)", "300"),
             new ChoiceField("target", "Look for", Targets), new PictureField(), new TextField("value", "Text to look for (when looking for text)"),
             new IntField("confidencePct", "Confidence % (1–100)", "90"), new BoolField("matchColor", "Match colors", true),
             new IntField("maxTurns", "Most swipes before giving up", "12"),
             new ChoiceField("whenFound", "When found", WhenFound),
             new ChoiceField("onFail", "If it gives up", OnFails), new TextField("failLabel", "Label to go to (for Go to a label)")],
            b => $"Swipe until {Looking(b)} shows{Tap(b)}"),
        new(BlockTypes.WalkTo, "Walk to picture", "Touch",
            () => new() { ["from"] = new JsonObject { ["x"] = 300, ["y"] = 1700 }, ["to"] = new JsonObject { ["x"] = 300, ["y"] = 1500 },
                          ["target"] = "image", ["confidencePct"] = 90, ["tolerancePx"] = 200, ["timeoutMs"] = 30_000, ["whenFound"] = "none" },
            [new SpotField("from", "Joystick middle"), new SpotField("to", "Joystick pushed forward"),
             new ChoiceField("target", "Walk to", Targets), new PictureField(), new TextField("value", "Text to walk to (when walking to text)"),
             new IntField("confidencePct", "Confidence % (1–100)", "90"), new BoolField("matchColor", "Match colors", true),
             new IntField("tolerancePx", "Arrived when it's this close to the middle of the screen (px)", "200"),
             new ConditionField(Optional: true, Title: "Or stop walking when"),
             new TimeoutField("timeoutMs", "Give up after (ms, blank = Infinite)"),
             new ChoiceField("whenFound", "When there", [("none", "Just go on"), ("tap", "Tap it, then go on")]),
             new ChoiceField("onFail", "If it gives up", OnFails), new TextField("failLabel", "Label to go to (for Go to a label)")],
            b => $"Walk to {Looking(b)}{Tap(b)}"),
        new(BlockTypes.DeviceButton, "Phone button", "Touch", () => new() { ["button"] = "back" },
            [new ChoiceField("button", "Button", Buttons)],
            b => $"Press {Text(Buttons, b.GetString("button", "back"))}"),

        new(BlockTypes.Key, "Key", "Keys", () => new() { ["key"] = "Enter", ["action"] = "press" },
            [new TextField("key", "Key (A, 5, Enter, Space, Backspace, Tab, Up, Shift, F1…)"),
             new ChoiceField("action", "Do", [("press", "Press it"), ("down", "Hold it down"), ("up", "Release it")]),
             new IntField("holdMs", "Press length (ms)", "30")],
            b => b.GetString("action", "press") switch
            {
                "down" => $"Hold {b.GetString("key", "")} down",
                "up" => $"Release {b.GetString("key", "")}",
                _ => $"Press {b.GetString("key", "")}",
            }),
        new(BlockTypes.HoldKey, "Hold key", "Keys", () => new() { ["key"] = "Space", ["holdMs"] = 1000 },
            [new TextField("key", "Key"), new IntField("holdMs", "Hold for (ms)", "1000", Required: true)],
            b => $"Hold {b.GetString("key", "")} for {b.GetInt("holdMs", 0)} ms"),
        new(BlockTypes.Type, "Type text", "Keys", () => new() { ["text"] = "", ["delayMs"] = 30 },
            [new TextField("text", "Text to type (goes to the selected text box, Android 13+)"),
             new IntField("delayMs", "Wait between letters (ms, 0 = all at once)", "30")],
            b => $"Type \"{b.GetString("text", "")}\""),

        new(BlockTypes.AppOpen, "Open app", "Apps", () => new() { ["package"] = "" },
            [new AppField("package", "App"), new BoolField("wait", "Wait until it's in front", true), new TimeoutField("timeoutMs", "Give up after (ms, 0 = Infinite)", BlankIsInfinite: false, Hint: "30000")],
            b => $"Open {AppName(b.GetString("package"))}"),
        new(BlockTypes.WaitForApp, "Wait for app", "Apps", () => new() { ["package"] = "" },
            [new AppField("package", "App"), new TimeoutField("timeoutMs", "Give up after (ms, blank = Infinite)")],
            b => $"Wait for {AppName(b.GetString("package"))}"),

        new(BlockTypes.Wait, "Wait", "Timing", () => new() { ["ms"] = 500 },
            [new IntField("ms", "Wait (ms)", "500", Required: true)],
            b => $"Wait {b.GetInt("ms", 0)} ms"),
        new(BlockTypes.WaitRandom, "Random wait", "Timing", () => new() { ["minMs"] = 300, ["maxMs"] = 800 },
            [new IntField("minMs", "At least (ms)", "300", Required: true), new IntField("maxMs", "At most (ms)", "800", Required: true)],
            b => $"Wait {b.GetInt("minMs", 0)}–{b.GetInt("maxMs", 0)} ms"),

        new(BlockTypes.WaitForText, "Wait for text", "Screen", () => new() { ["text"] = "", ["match"] = "contains", ["timeoutMs"] = 10_000 },
            Tapping(TextFields), b => $"Wait for \"{b.GetString("text", "")}\"{Tap(b)}"),
        new(BlockTypes.WaitForTextGone, "Wait until text is gone", "Screen", () => new() { ["text"] = "", ["match"] = "contains", ["timeoutMs"] = 10_000 },
            TextFields, b => $"Wait until \"{b.GetString("text", "")}\" is gone"),
        new(BlockTypes.WaitForImage, "Wait for picture", "Screen", () => new() { ["confidencePct"] = 90, ["timeoutMs"] = 10_000 },
            Tapping(PictureFields), b => b.GetString("imageData") is { Length: > 0 } ? $"Wait for picture · {b.GetInt("confidencePct", 90)}%{Tap(b)}" : "Wait for picture (none yet)"),
        new(BlockTypes.WaitForImageGone, "Wait until picture is gone", "Screen", () => new() { ["confidencePct"] = 90, ["timeoutMs"] = 10_000 },
            PictureFields, b => b.GetString("imageData") is { Length: > 0 } ? "Wait until picture is gone" : "Wait until picture is gone (none yet)"),
        new(BlockTypes.DetectPixelColor, "Wait for color", "Screen", () => new() { ["x"] = 540, ["y"] = 1200, ["color"] = "#FFFFFF", ["tolerance"] = 10, ["timeoutMs"] = 5000 },
            Tapping(PixelFields), b => $"Wait for {b.GetString("color", "")} at {b.GetInt("x", 0)}, {b.GetInt("y", 0)}{Tap(b)}"),
        new(BlockTypes.DetectPixelColorGone, "Wait until color is gone", "Screen", () => new() { ["x"] = 540, ["y"] = 1200, ["color"] = "#FFFFFF", ["tolerance"] = 10, ["timeoutMs"] = 5000 },
            PixelFields, b => $"Wait until {b.GetInt("x", 0)}, {b.GetInt("y", 0)} isn't {b.GetString("color", "")}"),

        new(BlockTypes.Loop, "Loop", "Logic", () => new() { ["count"] = 5 }, [new LoopCountField()],
            b => b.GetBool("forever") == true ? "Loop forever" : $"Loop {b.GetInt("count", 1)} times"),
        new(BlockTypes.If, "If", "Logic", () => new() { ["condition"] = new JsonObject { ["kind"] = "appInFront", ["package"] = "" } },
            [new ConditionField()], b => $"If {DescribeCondition(b.GetObject("condition"))}"),
        new(BlockTypes.Else, "Else", "Logic", () => [], [], _ => "Else"),
        new(BlockTypes.RepeatUntil, "Repeat until", "Logic",
            () => new() { ["condition"] = new JsonObject { ["kind"] = "appInFront", ["package"] = "" } },
            [new ConditionField(), new IntField("maxIterations", "Max tries (blank = Infinite)", "Infinite")],
            b => $"Repeat until {DescribeCondition(b.GetObject("condition"))}"),
        new(BlockTypes.End, "End", "Logic", () => [], [], _ => "End"),
        new(BlockTypes.Label, "Label", "Logic", () => new() { ["name"] = "start" }, [new TextField("name", "Label name")],
            b => $"Label \"{b.GetString("name", "")}\""),
        new(BlockTypes.Goto, "Go to label", "Logic", () => new() { ["name"] = "start" }, [new TextField("name", "Label name")],
            b => $"Go to \"{b.GetString("name", "")}\""),
        new(BlockTypes.Stop, "Stop macro", "Logic", () => [], [], _ => "Stop the macro"),

        new(BlockTypes.Notify, "Notify", "Utility", () => new() { ["message"] = "Done!", ["kind"] = "toast" },
            [new TextField("message", "Message"), new ChoiceField("kind", "Show as", [("toast", "Message"), ("sound", "Sound"), ("both", "Both")])],
            b => $"Notify \"{b.GetString("message", "")}\""),
        new(BlockTypes.PlaySound, "Play sound", "Utility", () => new() { ["sound"] = "default" }, [], _ => "Play a sound"),
        new(BlockTypes.RunMacro, "Run macro", "Utility", () => new() { ["macroName"] = "" }, [new MacroField("macroName", "Macro")],
            b => $"Run \"{b.GetString("macroName", "")}\""),
        new(BlockTypes.Pause, "Pause macro", "Utility", () => new() { ["target"] = "" }, [new MacroField("target", "Macro to pause")],
            b => $"Pause \"{b.GetString("target", "")}\""),
        new(BlockTypes.Resume, "Resume macro", "Utility", () => new() { ["target"] = "" }, [new MacroField("target", "Macro to resume")],
            b => $"Resume \"{b.GetString("target", "")}\""),
        new(BlockTypes.Comment, "Comment", "Utility", () => new() { ["text"] = "" }, [new TextField("text", "Comment")],
            b => $"// {b.GetString("text", "")}"),
        new(BlockTypes.Description, "Description", "Utility", () => new() { ["text"] = "" }, [new TextField("text", "Description", Multiline: true)],
            b => b.GetString("text", "") is { Length: > 0 } t ? t : "Description"),
    ];

    public static BlockKind? For(string type) => All.FirstOrDefault(k => k.Type == type);

    /// <summary>Installed apps' names by package, filled by the app picker; packages are shown until then.</summary>
    public static readonly Dictionary<string, string> AppNames = [];

    public static string AppName(string? package) =>
        string.IsNullOrEmpty(package) ? "(pick an app)" : AppNames.TryGetValue(package, out var name) ? name : package;

    private static string Tap(Block b) => b.GetString("whenFound", "none") is "tap" or "click" ? " · tap it" : "";
    private static string Looking(Block b) => b.GetString("target", "image") == "text"
        ? $"\"{b.GetString("value", "")}\""
        : b.GetString("imageData") is { Length: > 0 } ? "the picture" : "a picture (none yet)";

    public static string DescribeCondition(JsonObject? c)
    {
        var kind = Str(c?["kind"]);
        return kind switch
        {
            "appInFront" => $"{AppName(Str(c!["package"]))} is open",
            "textVisible" => $"\"{Str(c!["text"])}\" is on screen",
            "imageVisible" => "the picture is on screen",
            "pixelColor" => $"{Str(c!["color"])} is at {Num(c!["x"])}, {Num(c!["y"])}",
            "not" when c!["condition"] is JsonObject inner => Str(inner["kind"]) switch
            {
                "appInFront" => $"{AppName(Str(inner["package"]))} is not open",
                "textVisible" => $"\"{Str(inner["text"])}\" is not on screen",
                "imageVisible" => "the picture is not on screen",
                "pixelColor" => $"{Num(inner["x"])}, {Num(inner["y"])} is not {Str(inner["color"])}",
                _ => "NOT: " + DescribeCondition(inner),
            },
            null => "(no check)",
            _ => "a check made elsewhere",
        };
    }

    private static int Num(JsonNode? node) => (node as JsonValue)?.TryGetValue(out int i) == true ? i : 0;

    public static string? Str(JsonNode? node) => (node as JsonValue)?.TryGetValue(out string? s) == true ? s : null;

    private static string Text((string Value, string Text)[] choices, string value) =>
        choices.FirstOrDefault(c => c.Value == value).Text ?? value;

    /// <summary>How far each block is indented: one step inside If, Loop and Repeat until.</summary>
    public static int[] Depths(IReadOnlyList<Block> blocks)
    {
        var depths = new int[blocks.Count];
        var depth = 0;
        for (var i = 0; i < blocks.Count; i++)
        {
            var type = blocks[i].Type;
            if (type is BlockTypes.End or BlockTypes.Else) depth = Math.Max(0, depth - 1);
            depths[i] = depth;
            if (type is BlockTypes.If or BlockTypes.Loop or BlockTypes.RepeatUntil or BlockTypes.Else) depth++;
        }
        return depths;
    }
}
