using Android.Views;
using NightLoop.Engine.Input;
using NightLoop.Engine.Runtime;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Key, Hold key and Type on Android. Android doesn't let apps press keys in other apps, but since Android 13 an
/// accessibility service can type into the text box that has focus, like a keyboard. Night-loop does that.
/// </summary>
internal sealed class AccessibilityKeys : IInputSender
{
    public static readonly AccessibilityKeys Instance = new();

    private static Android.AccessibilityServices.InputMethod.AccessibilityInputConnection Connection()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33))
            throw new MacroFaultException("Typing and keys need Android 13 or newer.");
        var service = NightLoopAccessibilityService.Current
            ?? throw new MacroFaultException("Night-loop's accessibility service is off. Turn it on in Night-loop's Set up.");
        return service.InputMethod?.CurrentInputConnection
            ?? throw new MacroFaultException("No text box is selected, so there's nowhere to type. Tap a text box first (a Tap step works).");
    }

    private static Android.AccessibilityServices.InputMethod.AccessibilityInputConnection? UpConnection() =>
        OperatingSystem.IsAndroidVersionAtLeast(33) ? NightLoopAccessibilityService.Current?.InputMethod?.CurrentInputConnection : null;

    public void KeyDown(int virtualKey) => Send(KeyEventActions.Down, virtualKey);
    public void KeyUp(int virtualKey) => Send(KeyEventActions.Up, virtualKey);

    private static void Send(KeyEventActions action, int virtualKey)
    {
        var code = ToAndroid(virtualKey) ?? throw new MacroFaultException("This key can't be pressed on Android.");
        // Enter (or Tab, Esc...) often closes the text box as soon as it goes down, so there's nothing left to
        // send the release to. That's not an error: the key was pressed. Only a missing text box on the way down is.
        if (action == KeyEventActions.Up && UpConnection() is null) return;
        var connection = Connection();
        var time = Android.OS.SystemClock.UptimeMillis();
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            Night.OnMain(SendKey(connection, new KeyEvent(time, time, action, code, 0)));
    }

    public void TypeChar(char c)
    {
        var connection = Connection();
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            Night.OnMain(Commit(connection, c.ToString()));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static Action SendKey(Android.AccessibilityServices.InputMethod.AccessibilityInputConnection connection, KeyEvent e) =>
        () => connection.SendKeyEvent(e);

    [System.Runtime.Versioning.SupportedOSPlatform("android33.0")]
    private static Action Commit(Android.AccessibilityServices.InputMethod.AccessibilityInputConnection connection, string text) =>
        () => connection.CommitText(new Java.Lang.String(text), 1, null);

    // Android has no mouse blocks; the validator never lets them into an Android macro.
    public void MouseDown(MouseButton button) { }
    public void MouseUp(MouseButton button) { }
    public void MoveTo(int x, int y) { }
    public void MoveBy(int dx, int dy) { }
    public void Scroll(int notches) { }
    public (int X, int Y) CursorPosition => (0, 0);

    /// <summary>Windows virtual-key codes (what the engine uses) to Android key codes.</summary>
    internal static Keycode? ToAndroid(int vk) => vk switch
    {
        >= 0x41 and <= 0x5A => Keycode.A + (vk - 0x41),
        >= 0x30 and <= 0x39 => Keycode.Num0 + (vk - 0x30),
        >= 0x60 and <= 0x69 => Keycode.Numpad0 + (vk - 0x60),
        >= 0x70 and <= 0x7B => Keycode.F1 + (vk - 0x70),
        0x08 => Keycode.Del,
        0x09 => Keycode.Tab,
        0x0D => Keycode.Enter,
        0x1B => Keycode.Escape,
        0x20 => Keycode.Space,
        0x21 => Keycode.PageUp,
        0x22 => Keycode.PageDown,
        0x23 => Keycode.MoveEnd,
        0x24 => Keycode.MoveHome,
        0x25 => Keycode.DpadLeft,
        0x26 => Keycode.DpadUp,
        0x27 => Keycode.DpadRight,
        0x28 => Keycode.DpadDown,
        0x2D => Keycode.Insert,
        0x2E => Keycode.ForwardDel,
        0x10 or 0xA0 => Keycode.ShiftLeft,
        0xA1 => Keycode.ShiftRight,
        0x11 or 0xA2 => Keycode.CtrlLeft,
        0xA3 => Keycode.CtrlRight,
        0x12 or 0xA4 => Keycode.AltLeft,
        0xA5 => Keycode.AltRight,
        0x6A => Keycode.NumpadMultiply,
        0x6B => Keycode.NumpadAdd,
        0x6D => Keycode.NumpadSubtract,
        0x6E => Keycode.NumpadDot,
        0x6F => Keycode.NumpadDivide,
        0xBA => Keycode.Semicolon,
        0xBB => Keycode.Equals,
        0xBC => Keycode.Comma,
        0xBD => Keycode.Minus,
        0xBE => Keycode.Period,
        0xBF => Keycode.Slash,
        0xC0 => Keycode.Grave,
        0xDB => Keycode.LeftBracket,
        0xDC => Keycode.Backslash,
        0xDD => Keycode.RightBracket,
        0xDE => Keycode.Apostrophe,
        _ => null,
    };
}
