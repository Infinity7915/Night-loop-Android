using Android.Content;
using Android.Media;
using Android.OS;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Engine.Compiler;
using NightLoop.Engine.Runtime;

namespace NightLoop.Mobile.Core;

/// <summary>
/// The app's shared state, used by the screens and the bubble alike: the account, the macro list and the
/// macros that are playing.
/// </summary>
internal static class Night
{
    public const string ServerUrl = "https://api.night-loop.win/v1/";

    internal static readonly Handler MainHandler = new(Looper.MainLooper!);
    private static readonly Dictionary<Guid, MacroRun> RunsById = [];
    private static Context _app = null!;
    private static bool _started;

    public static ApiClient Api { get; private set; } = null!;
    public static AccountSession Session { get; private set; } = null!;
    public static RunManager Runs { get; private set; } = null!;
    /// <summary>Settings' extra waits, read live by running macros.</summary>
    public static BlockWaits Waits { get; } = new();

    /// <summary>Every macro on the account, newest first. Android macros play here; PC macros are listed only.</summary>
    public static IReadOnlyList<MacroSummaryDto> Macros { get; private set; } = [];
    public static int MacroLimit { get; private set; }

    /// <summary>Raised on the main thread when the list changes.</summary>
    public static event Action? MacrosChanged;
    /// <summary>Raised on the main thread when a macro starts or stops.</summary>
    public static event Action? RunsChanged;
    /// <summary>Raised on the main thread when the account signs out or another device takes over.</summary>
    public static event Action<string>? SignedOut;

    public static void Init(Context context)
    {
        if (_started) return;
        _started = true;
        _app = context.ApplicationContext!;
        CrashLog.Install(_app);
        AppInfo.Load(_app);
        LocalSettings.Load(_app);
        Waits.AfterClicksMs = LocalSettings.WaitAfterTapsMs;
        Waits.AfterBlocksMs = LocalSettings.WaitAfterBlocksMs;

        var http = new HttpClient { BaseAddress = new Uri(ServerUrl), Timeout = TimeSpan.FromSeconds(30) };
        Api = new ApiClient(http, AppInfo.InstallId(_app), AppInfo.Version, MacroPlatforms.Android);
        Session = new AccountSession(Api, new KeystoreSessionStore(System.IO.Path.Combine(_app.FilesDir!.AbsolutePath, "session.bin")),
            PhoneIdentity.Create(_app));
        Session.SignedOut += e => OnMain(() =>
        {
            StopAll();
            Macros = [];
            SignedOut?.Invoke(e.Message);
        });
        Session.OfflineGraceExpired += () => OnMain(StopAll);

        Runs = new RunManager(AccessibilityKeys.Instance)
        {
            Touch = AccessibilityTouch.Instance,
            Screen = ScreenCapture.Instance,
            Text = PhoneTextReader.Instance,
            Notify = message => OnMain(() => Screens.Ui.Toast(_app, message)),
            PlaySound = _ => OnMain(PlayDefaultSound),
            RunMacro = RunByNameAsync,
            Waits = Waits,
        };
        Runs.RunStateChanged += (run, state) => OnMain(() =>
        {
            if (state == RunState.Faulted)
                Screens.Ui.Toast(_app, $"\"{run.Name}\" stopped: {run.FaultMessage}");
            RunsChanged?.Invoke();
        });
    }

    /// <summary>
    /// What a test look in the editor ("Test on screen now") needs: the screen, text reading and which app is in
    /// front. A test look sends no taps or keys.
    /// </summary>
    public static RunEnvironment LookEnvironment() => new()
    {
        Input = AccessibilityKeys.Instance,
        Touch = AccessibilityTouch.Instance,
        Screen = ScreenCapture.Instance,
        Text = PhoneTextReader.Instance,
    };

    public static void OnMain(Action action)
    {
        if (Looper.MyLooper() == Looper.MainLooper) action();
        else MainHandler.Post(action);
    }

    // ---- Macro list ----

    public static async Task RefreshMacrosAsync()
    {
        var list = await Api.ListMacrosAsync();
        OnMain(() =>
        {
            Macros = [.. list.Macros.OrderByDescending(m => m.UpdatedAt)];
            MacroLimit = list.Limit;
            MacrosChanged?.Invoke();
        });
    }

    public static IEnumerable<MacroSummaryDto> AndroidMacros => Macros.Where(m => m.Platform == MacroPlatforms.Android);

    /// <summary>A new, empty Android macro on the account. Returns its id.</summary>
    public static async Task<Guid> CreateMacroAsync(string name)
    {
        var doc = new MacroDoc { Name = name, Platform = MacroPlatforms.Android };
        var macro = await Api.CreateMacroAsync(name, doc);
        await RefreshMacrosAsync();
        return macro.Id;
    }

    // ---- Playing ----

    /// <summary>The run started for a macro, while it plays.</summary>
    public static MacroRun? RunOf(Guid id) => RunsById.TryGetValue(id, out var run) && run.IsActive ? run : null;

    public static bool IsRunning(Guid id) => RunsById.TryGetValue(id, out var run) && run.IsActive;
    public static bool AnyRunning => RunsById.Values.Any(r => r.IsActive);

    /// <summary>
    /// Why taps can't be sent. When the switch is on but Android isn't running the service (it stopped it, often
    /// after an update or a crash), turning it off and on again restarts it.
    /// </summary>
    private static string AccessibilityOffMessage() => Setup.Permissions.AccessibilityOn(_app)
        ? "Android stopped Night-loop's accessibility service. In Accessibility settings, turn Night-loop off and on again."
        : "Turn on Night-loop in Accessibility settings first (see Set up).";

    /// <summary>Starts a saved Android macro (its draft if it was never saved). Returns why it can't, or null.</summary>
    public static async Task<string?> PlayAsync(Guid id)
    {
        if (!AccessibilityTouch.Ready) return AccessibilityOffMessage();
        try
        {
            var macro = await Api.GetMacroAsync(id);
            if (macro.Platform != MacroPlatforms.Android) return "PC macros play in Night-loop on your PC.";
            var doc = macro.SavedDoc ?? macro.DraftDoc;
            if (doc is null || doc.Blocks.Count == 0) return $"\"{macro.Name}\" has no blocks yet.";
            return await PlayAsync(id, doc);
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            return ex.Message;
        }
    }

    /// <summary>Plays a document as it is (the editor's Play uses the copy on screen).</summary>
    public static async Task<string?> PlayAsync(Guid id, MacroDoc doc)
    {
        if (!AccessibilityTouch.Ready) return AccessibilityOffMessage();
        if (ScreenCapture.Needs(doc) && !ScreenCapture.Ready)
        {
            ScreenConsentActivity.Ask(_app);
            return "This macro looks at the screen. Tap \"Start now\" in Android's window, then press Play again.";
        }
        CompiledMacro compiled;
        try
        {
            compiled = MacroCompiler.Compile(doc);
        }
        catch (MacroCompileException ex)
        {
            return ex.Issues.Count > 0 ? ex.Issues[0].Message : ex.Message;
        }
        Stop(id);
        // A short pause before the first step (Settings › Start delay): the bubble panel, Night-loop's screens and
        // the game's own window are still moving when Play is tapped, and a tap sent at that moment does nothing.
        await Task.Delay(LocalSettings.StartDelayMs);
        try
        {
            var run = Runs.Start(compiled);
            RunsById[id] = run;
            RunsChanged?.Invoke();
            return null;
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
    }

    public static void Stop(Guid id)
    {
        if (RunsById.Remove(id, out var run)) run.Stop();
        RunsChanged?.Invoke();
    }

    public static void StopAll()
    {
        foreach (var run in RunsById.Values) run.Stop();
        RunsById.Clear();
        RunsChanged?.Invoke();
    }

    private static async Task RunByNameAsync(string name, CancellationToken ct)
    {
        var summary = AndroidMacros.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new MacroFaultException($"There's no Android macro called \"{name}\".");
        var macro = await Api.GetMacroAsync(summary.Id, ct);
        var doc = macro.SavedDoc ?? macro.DraftDoc ?? throw new MacroFaultException($"\"{name}\" is empty.");
        doc.Options.RunAlongside = true; // never stop the macro that called it
        doc.Options.Repeat = new RepeatOptions { Mode = RepeatModes.Count, Count = 1 };
        var run = Runs.Start(MacroCompiler.Compile(doc));
        await using (ct.Register(run.Stop))
            await run.Completion;
        if (run.State == RunState.Faulted)
            throw new MacroFaultException($"\"{name}\" stopped with an error: {run.FaultMessage}");
    }

    private static void PlayDefaultSound()
    {
        try
        {
            RingtoneManager.GetRingtone(_app, RingtoneManager.GetDefaultUri(RingtoneType.Notification))?.Play();
        }
        catch (Java.Lang.Exception)
        {
            // No sound available: carry on quietly.
        }
    }
}
