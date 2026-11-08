using System.Text.Json.Nodes;
using NightLoop.Contracts.MacroDocument;

namespace NightLoop.Mobile.Editor;

/// <summary>One setting on a block, shown as one control in the edit window.</summary>
internal abstract record Field(string Key, string Label);
/// <summary>A whole number. Blank removes it (the block's default), unless <see cref="Required"/>.</summary>
internal sealed record IntField(string Key, string Label, string Hint, bool Required = false) : Field(Key, Label);
/// <summary>A spot on the screen: x and y at the top level (Key "") or inside an object (from / to).</summary>
internal sealed record SpotField(string Key, string Label) : Field(Key, Label);
internal sealed record ChoiceField(string Key, string Label, (string Value, string Text)[] Choices) : Field(Key, Label);
internal sealed record TextField(string Key, string Label, bool Multiline = false) : Field(Key, Label);
internal sealed record BoolField(string Key, string Label, bool Default) : Field(Key, Label);
/// <summary>An installed app's package name, with a picker.</summary>
internal sealed record AppField(string Key, string Label) : Field(Key, Label);
/// <summary>Another Android macro, by name.</summary>
internal sealed record MacroField(string Key, string Label) : Field(Key, Label);
/// <summary>If / Repeat until: "App in front" (or not).</summary>
internal sealed record ConditionField() : Field("condition", "Check");
/// <summary>Loop count, where blank means Infinite (stored as forever: true).</summary>
internal sealed record LoopCountField() : Field("count", "Times (blank = Infinite)");

internal sealed record BlockKind(string Type, string Name, string Group, Func<JsonObject> Defaults, Field[] Fields, Func<Block, string> Summary);

/// <summary>The blocks the Android editor offers, in the order of the Add block list.</summary>
internal static class BlockKinds
{
    private static readonly (string, string)[] Buttons = [("back", "Back"), ("home", "Home"), ("recents", "Recent apps"), ("notifications", "Notifications")];

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
        new(BlockTypes.DeviceButton, "Phone button", "Touch", () => new() { ["button"] = "back" },
            [new ChoiceField("button", "Button", Buttons)],
            b => $"Press {Text(Buttons, b.GetString("button", "back"))}"),

        new(BlockTypes.AppOpen, "Open app", "Apps", () => new() { ["package"] = "" },
            [new AppField("package", "App"), new BoolField("wait", "Wait until it's in front", true), new IntField("timeoutMs", "Give up after (ms, 0 = Infinite)", "30000")],
            b => $"Open {AppName(b.GetString("package"))}"),
        new(BlockTypes.WaitForApp, "Wait for app", "Apps", () => new() { ["package"] = "" },
            [new AppField("package", "App"), new IntField("timeoutMs", "Give up after (ms, blank = Infinite)", "Infinite")],
            b => $"Wait for {AppName(b.GetString("package"))}"),

        new(BlockTypes.Wait, "Wait", "Timing", () => new() { ["ms"] = 500 },
            [new IntField("ms", "Wait (ms)", "500", Required: true)],
            b => $"Wait {b.GetInt("ms", 0)} ms"),
        new(BlockTypes.WaitRandom, "Random wait", "Timing", () => new() { ["minMs"] = 300, ["maxMs"] = 800 },
            [new IntField("minMs", "At least (ms)", "300", Required: true), new IntField("maxMs", "At most (ms)", "800", Required: true)],
            b => $"Wait {b.GetInt("minMs", 0)}–{b.GetInt("maxMs", 0)} ms"),

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

    public static string DescribeCondition(JsonObject? c)
    {
        var kind = Str(c?["kind"]);
        return kind switch
        {
            "appInFront" => $"{AppName(Str(c!["package"]))} is in front",
            "not" when c!["condition"] is JsonObject inner && Str(inner["kind"]) == "appInFront" =>
                $"{AppName(Str(inner["package"]))} is not in front",
            null => "(no check)",
            _ => "a check made elsewhere",
        };
    }

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
