using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using NightLoop.Client;
using NightLoop.Contracts.Api;
using NightLoop.Contracts.MacroDocument;
using NightLoop.Mobile.Core;
using NightLoop.Mobile.Screens;
using NightLoop.Mobile.Setup;

namespace NightLoop.Mobile;

/// <summary>
/// Night-loop's main screen: sign in (with the emailed code), then the setup checklist, the bubble switch and the
/// macro list. Android macros play and open in the editor; PC macros are listed as "PC only".
/// </summary>
[Activity(Name = "win.nightloop.app.MainActivity", Label = "Night-loop", MainLauncher = true, Exported = true,
          LaunchMode = LaunchMode.SingleTop, Icon = "@drawable/ic_nightloop",
          WindowSoftInputMode = SoftInput.AdjustResize,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private enum Screen { Loading, SignIn, Code, Home }

    private Screen _screen = Screen.Loading;
    private Guid _challenge;
    private bool _unlocking;
    private string _codeEmail = "";
    private string _message = "";
    private string _email = "";

    private LinearLayout? _steps;
    private Button? _bubbleButton;
    private TextView? _bubbleNote;
    private LinearLayout? _macroList;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Night.Init(this);
        Night.MacrosChanged += OnMacrosChanged;
        Night.RunsChanged += OnMacrosChanged;
        Night.SignedOut += OnSignedOut;
        Show(Screen.Loading);
        _ = RestoreAsync();
    }

    protected override void OnDestroy()
    {
        Night.MacrosChanged -= OnMacrosChanged;
        Night.RunsChanged -= OnMacrosChanged;
        Night.SignedOut -= OnSignedOut;
        base.OnDestroy();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_screen == Screen.Home)
        {
            RefreshSetup();
            _ = LoadMacrosAsync();
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (_screen == Screen.Home) RefreshSetup();
    }

    private void OnMacrosChanged()
    {
        if (_screen == Screen.Home) FillMacros();
    }

    private void OnSignedOut(string message)
    {
        _message = message;
        Show(Screen.SignIn);
    }

    // ---- Account ----

    private async Task RestoreAsync()
    {
        if (Night.Session.IsSignedIn)
        {
            Show(Screen.Home);
            return;
        }
        try
        {
            await Night.Session.TryRestoreAsync();
            RunOnUiThread(AfterRestore);
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() =>
            {
                _message = ex.Message;
                Show(Screen.SignIn);
            });
        }
    }

    private void AfterRestore()
    {
        if (Night.Session.PendingUnlock is { } unlock)
        {
            _unlocking = true;
            _challenge = unlock;
            _codeEmail = Night.Session.PendingUnlockEmail ?? "";
            _message = Night.Session.PendingUnlockError ?? "";
            Show(Screen.Code);
        }
        else Show(Night.Session.IsSignedIn ? Screen.Home : Screen.SignIn);
    }

    private async Task SignInAsync(string email, string password, Button button)
    {
        button.Enabled = false;
        try
        {
            var result = await Night.Session.SignInAsync(email, password);
            RunOnUiThread(() =>
            {
                _message = "";
                if (result is SignInResult.NeedsCode code)
                {
                    _unlocking = false;
                    _challenge = code.ChallengeId;
                    _codeEmail = email;
                    Show(Screen.Code);
                }
                else Show(Screen.Home);
            });
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() =>
            {
                _message = ex.Message;
                Show(Screen.SignIn);
            });
        }
    }

    private async Task VerifyAsync(string code, bool trust, Button button)
    {
        button.Enabled = false;
        try
        {
            if (_unlocking) await Night.Session.VerifyUnlockAsync(code);
            else await Night.Session.VerifySignInAsync(_challenge, code, trust);
            RunOnUiThread(() =>
            {
                _message = "";
                Show(Screen.Home);
            });
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            RunOnUiThread(() =>
            {
                _message = ex.Message;
                Show(Screen.Code);
            });
        }
    }

    private async Task ResendAsync()
    {
        try
        {
            _challenge = _unlocking ? await Night.Session.ResendUnlockAsync() : await Night.Session.ResendCodeAsync(_challenge);
            RunOnUiThread(() => Ui.Toast(this, "A new code is on its way."));
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException or InvalidOperationException)
        {
            RunOnUiThread(() => Ui.Toast(this, ex.Message));
        }
    }

    private async Task SignOutAsync()
    {
        Night.StopAll();
        await Night.Session.SignOutAsync();
    }

    // ---- Screens ----

    private void Show(Screen screen)
    {
        _screen = screen;
        _steps = null;
        _macroList = null;
        var page = Ui.Column(this, Palette.Dp(this, 16));
        page.AddView(Ui.Text(this, "Night-loop", 26, Palette.Text, bold: true));
        switch (screen)
        {
            case Screen.Loading:
                page.AddView(Ui.Text(this, "Signing in…", 15, Palette.TextQuiet));
                break;
            case Screen.SignIn:
                BuildSignIn(page);
                break;
            case Screen.Code:
                BuildCode(page);
                break;
            case Screen.Home:
                BuildHome(page);
                break;
        }
        SetContentView(Ui.Scroll(this, page));
        if (screen == Screen.Home)
        {
            RefreshSetup();
            _ = LoadMacrosAsync();
        }
    }

    private void AddMessage(LinearLayout page)
    {
        if (_message.Length > 0) page.AddView(Ui.Text(this, _message, 14, Palette.Bad));
    }

    private void BuildSignIn(LinearLayout page)
    {
        page.AddView(Ui.Text(this, $"Android preview {AppInfo.Version}", 14, Palette.TextQuiet));
        page.AddView(Ui.Heading(this, "Sign in"));
        var card = Ui.Card(this);
        var email = Ui.Input(this, "Email", _email, InputTypes.ClassText | InputTypes.TextVariationEmailAddress);
        var password = Ui.Input(this, "Password", "", InputTypes.ClassText | InputTypes.TextVariationPassword);
        card.AddView(email);
        card.AddView(password);
        Button? button = null;
        button = Ui.Button(this, "Sign in", () =>
        {
            _email = email.Text?.Trim() ?? "";
            if (_email.Length == 0 || string.IsNullOrEmpty(password.Text)) return;
            _ = SignInAsync(_email, password.Text!, button!);
        });
        card.AddView(button);
        page.AddView(card);
        AddMessage(page);
        page.AddView(Ui.Text(this,
            "Use your Night-loop account. One device can be signed in at a time, so signing in here signs Night-loop out on your PC. " +
            "No account yet? Create one in Night-loop on your PC.", 13, Palette.TextQuiet));
    }

    private void BuildCode(LinearLayout page)
    {
        page.AddView(Ui.Heading(this, "Enter the code"));
        var card = Ui.Card(this);
        card.AddView(Ui.Text(this, _codeEmail.Length > 0 ? $"We emailed a 6-digit code to {_codeEmail}." : "We emailed you a 6-digit code.", 14, Palette.TextQuiet));
        var code = Ui.Input(this, "6-digit code", "", InputTypes.ClassNumber);
        card.AddView(code);
        var trust = new CheckBox(this) { Text = "Trust this phone (skip the code next time)", Checked = true };
        trust.SetTextColor(Palette.Text);
        if (!_unlocking) card.AddView(trust);
        Button? verify = null;
        verify = Ui.Button(this, "Continue", () =>
        {
            var text = code.Text?.Trim() ?? "";
            if (text.Length > 0) _ = VerifyAsync(text, trust.Checked, verify!);
        });
        var row = Ui.Row(this);
        row.AddView(verify);
        row.AddView(Ui.Button(this, "Resend code", () => _ = ResendAsync(), primary: false));
        card.AddView(row);
        page.AddView(card);
        AddMessage(page);
        page.AddView(Ui.Button(this, "Back", () =>
        {
            _message = "";
            Show(Screen.SignIn);
        }, primary: false));
    }

    private void BuildHome(LinearLayout page)
    {
        var user = Night.Session.User;
        var account = Ui.Row(this);
        account.AddView(Ui.Fill(Ui.Text(this, user is null ? "" : $"{user.Username} · {user.Email}", 14, Palette.TextQuiet)));
        account.AddView(Ui.Button(this, "Sign out", () => _ = SignOutAsync(), primary: false));
        page.AddView(account);

        _steps = Ui.Column(this, 0);
        page.AddView(_steps);

        page.AddView(Ui.Heading(this, "Your macros"));
        var actions = Ui.Row(this);
        actions.AddView(Ui.Button(this, "New macro", NewMacro));
        _bubbleButton = Ui.Button(this, "Show bubble", ToggleBubble, primary: false);
        actions.AddView(_bubbleButton);
        page.AddView(actions);
        _bubbleNote = Ui.Text(this, "", 13, Palette.TextQuiet);
        page.AddView(_bubbleNote);
        _macroList = Ui.Column(this, 0);
        _macroList.SetPadding(0, Palette.Dp(this, 10), 0, 0);
        page.AddView(_macroList);
        FillMacros();
    }

    private void RefreshSetup()
    {
        if (_steps is null) return;
        _steps.RemoveAllViews();
        var steps = Permissions.Steps(this);
        if (steps.Any(s => !s.Done)) _steps.AddView(Ui.Heading(this, "Set up"));
        foreach (var step in steps.Where(s => !s.Done))
        {
            var card = Ui.Card(this);
            var top = Ui.Row(this);
            top.AddView(Ui.Fill(Ui.Text(this, step.Title, 16, Palette.Text, bold: true)));
            top.AddView(Ui.Text(this, "Off", 14, Palette.Warn, bold: true));
            card.AddView(top);
            card.AddView(Ui.Text(this, step.Why, 14, Palette.TextQuiet));
            card.AddView(Ui.Button(this, "Turn on", () => step.Open(this)));
            _steps.AddView(card);
        }

        if (_bubbleButton is null || _bubbleNote is null) return;
        var canDraw = Permissions.CanDrawOverlays(this);
        _bubbleButton.Enabled = canDraw;
        _bubbleButton.Text = BubbleService.Running ? "Hide bubble" : "Show bubble";
        _bubbleNote.Text = canDraw
            ? "The bubble floats over your games. Tap it to play or stop a macro; drag it anywhere."
            : "Turn on \"Display over other apps\" above to use the bubble.";
    }

    private async Task LoadMacrosAsync()
    {
        try
        {
            await Night.RefreshMacrosAsync();
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() => Ui.Toast(this, ex.Message));
        }
    }

    private void FillMacros()
    {
        if (_macroList is null) return;
        _macroList.RemoveAllViews();
        if (Night.Macros.Count == 0)
        {
            _macroList.AddView(Ui.Text(this, "No macros yet. Tap New macro to make one.", 14, Palette.TextQuiet));
            return;
        }
        foreach (var macro in Night.Macros.OrderBy(m => m.Platform == MacroPlatforms.Android ? 0 : 1))
        {
            var card = Ui.Card(this);
            var row = Ui.Row(this);
            var info = Ui.Column(this, 0);
            var android = macro.Platform == MacroPlatforms.Android;
            var running = Night.IsRunning(macro.Id);
            info.AddView(Ui.Text(this, macro.Name, 16, android ? Palette.Text : Palette.TextQuiet, bold: true));
            var detail = android
                ? $"{macro.BlockCount} blocks{(macro.HasDraft ? " · unsaved changes" : "")}{(running ? " · playing" : "")}"
                : "PC only · plays in Night-loop on your PC";
            info.AddView(Ui.Text(this, detail, 13, running ? Palette.Good : Palette.TextQuiet));
            row.AddView(Ui.Fill(info));
            if (android)
            {
                var id = macro.Id;
                row.AddView(running
                    ? Ui.Small(this, "■", () => Night.Stop(id), $"Stop {macro.Name}")
                    : Ui.Small(this, "▶", () => _ = PlayAsync(id), $"Play {macro.Name}"));
                card.Click += (_, _) => OpenEditor(id);
            }
            card.AddView(row);
            _macroList.AddView(card);
        }
        if (Night.MacroLimit > 0)
            _macroList.AddView(Ui.Text(this, $"{Night.Macros.Count} / {Night.MacroLimit} macros used", 13, Palette.TextQuiet));
    }

    private async Task PlayAsync(Guid id)
    {
        var error = await Night.PlayAsync(id);
        RunOnUiThread(() =>
        {
            if (error is not null) Ui.Toast(this, error);
            else
            {
                // Out of the way, so the macro can tap the app underneath.
                Ui.Toast(this, "Playing. Use the bubble to stop it.");
                if (BubbleService.Running) MoveTaskToBack(true);
            }
        });
    }

    private void NewMacro()
    {
        var name = Ui.Input(this, "Macro name", "New macro");
        var box = Ui.Column(this, Palette.Dp(this, 20));
        box.AddView(name);
        new AlertDialog.Builder(this)
            .SetTitle("New Android macro")!
            .SetView(box)!
            .SetPositiveButton("Create", (_, _) =>
            {
                var text = name.Text?.Trim() ?? "";
                if (text.Length > 0) _ = CreateAsync(text);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private async Task CreateAsync(string name)
    {
        try
        {
            var id = await Night.CreateMacroAsync(name);
            RunOnUiThread(() => OpenEditor(id));
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() => Ui.Toast(this, ex.Message));
        }
    }

    private void OpenEditor(Guid id) =>
        StartActivity(new Intent(this, typeof(EditorActivity)).PutExtra(EditorActivity.MacroIdExtra, id.ToString()));

    private void ToggleBubble()
    {
        var intent = new Intent(this, typeof(BubbleService));
        if (BubbleService.Running)
            StartService(intent.SetAction(BubbleService.ActionStop));
        else
            StartForegroundService(intent);
        // The service flips Running when it starts or stops.
        _bubbleButton?.PostDelayed(RefreshSetup, 300);
    }
}
