using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.Runtime;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Screen;

namespace NightLoop.Mobile.Core;

/// <summary>
/// Sees the screen for the screen-reading blocks (Wait for text / image / pixel, and those checks), through Android's
/// screen capture. Android asks once per session ("Start recording or casting"); nothing is saved or sent anywhere.
/// </summary>
internal sealed class ScreenCapture : IScreenCapture
{
    public static readonly ScreenCapture Instance = new();

    private readonly object _gate = new();
    private MediaProjection? _projection;
    private VirtualDisplay? _display;
    private ImageReader? _reader;
    private byte[]? _frame;
    // The newest picture Android sent, kept until the next look turns it into _frame. Taking every picture as it
    // arrives matters: when nobody takes them, Android's queue fills up and stops, and the next look got a picture
    // from minutes ago (a macro "found" a picture that was on screen while it was being set up, not the one now).
    private global::Android.Media.Image? _latest;
    private global::Android.OS.HandlerThread? _thread;
    private byte[]? _raw;
    private int _width, _height, _dpi;
    private DisplayManager? _displays;

    public static bool Ready => Instance._projection is not null;

    public (int Left, int Top, int Width, int Height) Desktop
    {
        get
        {
            lock (_gate)
            {
                FollowTurn();
                return (0, 0, _width, _height);
            }
        }
    }

    /// <summary>Starts capturing with the permission Android just gave (called by the bubble's service).</summary>
    public void Start(Context context, int resultCode, Intent data)
    {
        Stop();
        MediaProjection? projection = null;
        ImageReader? reader = null;
        VirtualDisplay? display;
        try
        {
            var manager = (MediaProjectionManager)context.GetSystemService(Context.MediaProjectionService)!;
            projection = manager.GetMediaProjection(resultCode, data);
            if (projection is null) return;
            projection.RegisterCallback(new Ended(projection), new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!));
            var metrics = context.Resources!.DisplayMetrics!;
            var displays = (DisplayManager)context.GetSystemService(Context.DisplayService)!;
            var (w0, h0) = ScreenSize(displays) ?? RealSize(context);
            reader = ImageReader.NewInstance(w0, h0, (ImageFormatType)1 /* RGBA_8888 */, 3);
            var thread = new global::Android.OS.HandlerThread("Night-loop screen");
            thread.Start();
            lock (_gate) _thread = thread;
            reader.SetOnImageAvailableListener(new Fresh(reader), new global::Android.OS.Handler(thread.Looper!));
            display = projection.CreateVirtualDisplay("Night-loop", w0, h0, (int)metrics.DensityDpi,
                (global::Android.Views.DisplayFlags)(int)VirtualDisplayFlags.AutoMirror, reader.Surface, null, null);
        }
        catch (Java.Lang.Exception)
        {
            // Some phones and emulators refuse screen capture; screen reading just stays off instead of crashing.
            reader?.Close();
            try { projection?.Stop(); } catch (Java.Lang.Exception) { }
            lock (_gate) EndThread();
            return;
        }
        var (w, h) = (reader.Width, reader.Height);
        lock (_gate)
        {
            _projection = projection;
            _reader = reader;
            _display = display;
            _width = w;
            _height = h;
            _dpi = (int)context.Resources!.DisplayMetrics!.DensityDpi;
            _displays = (DisplayManager)context.GetSystemService(Context.DisplayService)!;
            _frame = null;
        }
    }

    /// <summary>
    /// Keeps the capture the same shape as the screen. Android doesn't turn it with the phone: a game going sideways
    /// was shrunk into the upright picture with bars above and below, so pictures were found at the wrong size and
    /// every tap went to the wrong spot. Called with the lock held.
    /// </summary>
    private void FollowTurn()
    {
        if (_display is null || _displays is null || _thread?.Looper is not { } looper) return;
        if (ScreenSize(_displays) is not { } size || (size.W == _width && size.H == _height)) return;
        var (w, h) = size;
        ImageReader? reader = null;
        try
        {
            reader = ImageReader.NewInstance(w, h, (ImageFormatType)1 /* RGBA_8888 */, 3);
            reader.SetOnImageAvailableListener(new Fresh(reader), new global::Android.OS.Handler(looper));
            _display.Resize(w, h, _dpi);
            _display.Surface = reader.Surface;
        }
        catch (Java.Lang.Exception)
        {
            reader?.Close();
            return;
        }
        DropLatest();
        _reader?.Close();
        _reader = reader;
        _width = w;
        _height = h;
        _frame = null;
    }

    /// <summary>The screen's size as it is turned now, in pixels.</summary>
    private static (int W, int H)? ScreenSize(DisplayManager displays)
    {
        try
        {
            if (displays.GetDisplay(0 /* the phone's screen */) is not { } screen) return null;
            var size = new Point();
#pragma warning disable CA1422, CS0618
            screen.GetRealSize(size);
#pragma warning restore CA1422, CS0618
            return size.X > 0 && size.Y > 0 ? (size.X, size.Y) : null;
        }
        catch (Java.Lang.Exception)
        {
            return null;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _display?.Release();
            DropLatest();
            _reader?.Close();
            EndThread();
            try { _projection?.Stop(); } catch (Java.Lang.Exception) { }
            _display = null;
            _reader = null;
            _projection = null;
            _displays = null;
            _frame = null;
        }
    }

    public ScreenImage Capture(int left, int top, int width, int height)
    {
        lock (_gate)
        {
            if (_reader is null) throw new NightLoop.Engine.Runtime.MacroFaultException("Night-loop can't see the screen. Press Play again and tap \"Start now\".");
            FollowTurn();
            Grab();
            // Right after starting or turning, the first picture can take a moment.
            for (var waited = 0; _frame is null && _reader is not null && waited < FirstFrameMs; waited += 50)
            {
                Monitor.Wait(_gate, 50);
                Grab();
            }
            if (_frame is null) throw new NightLoop.Engine.Runtime.MacroFaultException("The screen isn't ready yet. Try again in a moment.");
            var full = new ScreenImage(0, 0, _width, _height, _frame);
            return full.Crop(left, top, width, height);
        }
    }

    /// <summary>Longest a look waits for the first picture after the capture starts or turns.</summary>
    private const int FirstFrameMs = 1500;

    private void DropLatest()
    {
        try { _latest?.Close(); } catch (Java.Lang.Exception) { }
        _latest = null;
    }

    private void EndThread()
    {
        _thread?.QuitSafely();
        _thread = null;
    }

    /// <summary>Takes each picture as soon as Android sends it, so the screen copy never falls behind.</summary>
    private sealed class Fresh(ImageReader mine) : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        public void OnImageAvailable(ImageReader? reader)
        {
            lock (Instance._gate)
            {
                global::Android.Media.Image? image;
                try { image = mine.AcquireLatestImage(); }
                catch (Java.Lang.Exception) { return; } // this capture already ended
                if (image is null) return;
                // A picture from an older capture is dropped. One that comes before Start has handed this reader
                // over is kept: a still screen may not send another.
                if (Instance._reader is { } now && !ReferenceEquals(now, mine))
                {
                    image.Close();
                    return;
                }
                Instance.DropLatest();
                Instance._latest = image;
                Monitor.PulseAll(Instance._gate); // a look waiting for its first picture
            }
        }
    }

    /// <summary>Copies the newest picture (RGBA with row padding) into a BGRA array. No new picture = the screen hasn't changed.</summary>
    private void Grab()
    {
        var image = _latest;
        _latest = null;
        using var _ = image;
        if (image is null) return;
        var plane = image.GetPlanes()![0];
        var buffer = plane.Buffer!;
        var rowStride = plane.RowStride;
        var pixelStride = plane.PixelStride;
        var w = Math.Min(_width, image.Width);
        var h = Math.Min(_height, image.Height);
        // Reuse one buffer: a new ~10 MB array for every look piled up memory when the bubble ran for a long time.
        var size = buffer.Remaining();
        if (_raw is null || _raw.Length < size) _raw = new byte[size];
        var raw = _raw;
        buffer.Get(raw, 0, size);
        var frame = _frame is { Length: var n } f && n == _width * _height * 4 ? f : new byte[_width * _height * 4];
        for (var y = 0; y < h; y++)
        {
            var src = y * rowStride;
            var dst = y * _width * 4;
            for (var x = 0; x < w; x++, src += pixelStride, dst += 4)
            {
                frame[dst] = raw[src + 2];
                frame[dst + 1] = raw[src + 1];
                frame[dst + 2] = raw[src];
                frame[dst + 3] = 255;
            }
        }
        _frame = frame;
    }

    private static (int W, int H) RealSize(Context context)
    {
        var windows = context.GetSystemService(Context.WindowService)!.JavaCast<global::Android.Views.IWindowManager>()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var b = windows.CurrentWindowMetrics.Bounds;
            return (b.Width(), b.Height());
        }
        var m = new global::Android.Util.DisplayMetrics();
#pragma warning disable CA1422, CS0618
        windows.DefaultDisplay!.GetRealMetrics(m);
#pragma warning restore CA1422, CS0618
        return (m.WidthPixels, m.HeightPixels);
    }

    private sealed class Ended(MediaProjection mine) : MediaProjection.Callback
    {
        public override void OnStop() => Night.OnMain(() =>
        {
            lock (Instance._gate)
            {
                // An older capture ending must not switch off the one that replaced it.
                if (!ReferenceEquals(Instance._projection, mine)) return;
                Instance._display?.Release();
                Instance.DropLatest();
                Instance._reader?.Close();
                Instance.EndThread();
                Instance._display = null;
                Instance._reader = null;
                Instance._projection = null;
                Instance._displays = null;
                Instance._frame = null;
            }
        });
    }

    /// <summary>Whether a macro reads the screen, so Play asks for screen capture first.</summary>
    public static bool Needs(MacroDoc doc) => doc.Blocks.Any(b =>
        b.Type is BlockTypes.WaitForText or BlockTypes.WaitForTextGone or BlockTypes.WaitForImage or BlockTypes.WaitForImageGone
            or BlockTypes.DetectPixelColor or BlockTypes.DetectPixelColorGone or BlockTypes.SwipeScan or BlockTypes.WalkTo
        || (b.Params["condition"]?.ToJsonString() is { } c && (c.Contains("\"textVisible\"") || c.Contains("\"imageVisible\"") || c.Contains("\"pixelColor\""))));
}

/// <summary>
/// A see-through screen that asks Android for screen capture ("Start now"), then hands the answer to the bubble's
/// service, which must hold it (Android only lets a running screen-capture service use it).
/// </summary>
[Activity(Name = "win.nightloop.app.ScreenConsentActivity", Exported = false, Theme = "@android:style/Theme.Translucent.NoTitleBar",
          ExcludeFromRecents = true, NoHistory = true)]
public sealed class ScreenConsentActivity : Activity
{
    private const int RequestCode = 7;

    public static void Ask(Context context) =>
        context.StartActivity(new Intent(context, typeof(ScreenConsentActivity)).AddFlags(ActivityFlags.NewTask));

    protected override void OnCreate(global::Android.OS.Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var manager = (MediaProjectionManager)GetSystemService(MediaProjectionService)!;
        // Android 14+: only offer "entire screen". Sharing one app hides the game whenever another app (or the
        // game restarting) is in front, and the blocks would look at an old picture.
        var ask = OperatingSystem.IsAndroidVersionAtLeast(34)
            ? manager.CreateScreenCaptureIntent(MediaProjectionConfig.CreateConfigForDefaultDisplay())
            : manager.CreateScreenCaptureIntent();
        StartActivityForResult(ask, RequestCode);
    }

    protected override void OnActivityResult(int requestCode, [GeneratedEnum] Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == RequestCode && resultCode == Result.Ok && data is not null)
        {
            var intent = new Intent(this, typeof(BubbleService)).SetAction(BubbleService.ActionCapture)
                .PutExtra("code", (int)resultCode).PutExtra("data", data);
            StartForegroundService(intent);
            Screens.Ui.Toast(this, "Night-loop can see the screen now. Press Play again.");
        }
        else Screens.Ui.Toast(this, "Screen reading stays off. Blocks that look at the screen won't work.");
        Finish();
    }
}
