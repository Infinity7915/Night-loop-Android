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
[Activity(Name = "win.nightloop.app.MainActivity", Label = "Night-loop", Exported = true,
          LaunchMode = LaunchMode.SingleTop, Icon = "@drawable/ic_nightloop",
          WindowSoftInputMode = SoftInput.AdjustResize,
          ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize)]
public sealed class MainActivity : Activity
{
    private enum Screen { Loading, SignIn, SignUp, Code, Home, Settings }

    private Screen _screen = Screen.Loading;
    private Guid _challenge;
    private bool _unlocking;
    private bool _signingUp;
    private string _codeEmail = "";
    private string _message = "";
    private string _email = "";

    private LinearLayout? _steps;
    private Button? _bubbleButton;
    private TextView? _bubbleNote;
    private LinearLayout? _macroList;
    private LinearLayout? _updateBox;
    private TextView? _updateStatus;
    private static Updater.Update? _update;
    private static bool _updateChecked;
    private bool _installing;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Night.Init(this);
        Seasons.ApplyLauncherIcon(this);
        Night.MacrosChanged += OnMacrosChanged;
        Night.RunsChanged += OnMacrosChanged;
        Night.SignedOut += OnSignedOut;
        Show(Screen.Loading);
        _ = RestoreAsync();
        if (!_updateChecked) _ = CheckForUpdateAsync(quiet: true);
    }

    // ---- Updates ----

    private async Task CheckForUpdateAsync(bool quiet)
    {
        try
        {
            var update = await Updater.CheckAsync();
            _updateChecked = true;
            RunOnUiThread(() =>
            {
                _update = update;
                FillUpdate();
                if (!quiet && _updateStatus is not null)
                    _updateStatus.Text = update is null
                        ? $"You're on the latest release ({AppInfo.Version})."
                        : $"Night-loop {update.Version} is available. Tap Update now on Home.";
            });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (!quiet) RunOnUiThread(() =>
            {
                if (_updateStatus is not null) _updateStatus.Text = "Couldn't check for updates. Check your internet connection.";
            });
        }
    }

    private void FillUpdate()
    {
        if (_updateBox is null) return;
        _updateBox.RemoveAllViews();
        if (_update is not { } update) return;
        var card = Ui.Card(this);
        card.AddView(Ui.Text(this, $"Update available: {update.Version}", 16, Palette.Text, bold: true));
        var note = Ui.Text(this, $"You have {AppInfo.Version}. The update downloads ({update.Size / 1_048_576.0:0.#} MB) and Android asks you to confirm.", 14, Palette.TextQuiet);
        card.AddView(note);
        var button = Ui.Button(this, "Update now", () => { });
        button.Click += (_, _) => _ = InstallUpdateAsync(update, button, note);
        card.AddView(button);
        if (Updater.LastResult(this) is { } last)
            card.AddView(Ui.Text(this, $"Last try: {last}", 12, Palette.Warn));
        _updateBox.AddView(card);
    }

    private async Task InstallUpdateAsync(Updater.Update update, Button button, TextView note)
    {
        if (_installing) return;
        if (!Updater.CanInstall(this))
        {
            Ui.Toast(this, "Allow Night-loop to install updates, then tap Update now again.");
            Updater.OpenInstallPermission(this);
            return;
        }
        _installing = true;
        button.Enabled = false;
        try
        {
            var last = -1;
            // Off the main thread: writing the 60 MB .apk on it froze Night-loop.
            await Task.Run(() => Updater.InstallAsync(this, update, percent =>
            {
                if (percent == last) return;
                last = percent;
                RunOnUiThread(() => note.Text = $"Downloading… {percent}%");
            }));
            RunOnUiThread(() => note.Text = "Downloaded. Tap Update in Android's window. Night-loop closes while it installs; open it again from the notice or your apps.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException or Java.Lang.Exception)
        {
            RunOnUiThread(() =>
            {
                note.Text = $"The download failed ({ex.Message}). Try again.";
                button.Enabled = true;
            });
        }
        finally
        {
            _installing = false;
        }
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
        else if (_screen == Screen.Settings) Show(Screen.Settings); // a permission may have changed
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
            _signingUp = false;
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
                    _signingUp = false;
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
            else if (_signingUp) await Night.Session.VerifySignUpAsync(_challenge, code);
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

    /// <summary>Like the PC app: confirm with the password, then the account and its macros are removed.</summary>
    private void AskDeleteAccount()
    {
        var box = Ui.Column(this, Palette.Dp(this, 20));
        box.AddView(Ui.Text(this, "Your account and every macro on it are removed. You have 30 days to change your mind by contacting support; after that it's gone for good.", 14, Palette.Text));
        var password = Ui.Input(this, "Your password", type: InputTypes.ClassText | InputTypes.TextVariationPassword);
        password.FilterTouchesWhenObscured = true;
        box.AddView(password);
        var error = Ui.Text(this, "", 13, Palette.Warn);
        box.AddView(error);
        var dialog = Dialogs.Builder(this)
            .SetTitle("Delete account")!
            .SetView(box)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetPositiveButton("Delete account", (_, _) => { })!
            .Create()!;
        dialog.Show();
        var delete = dialog.GetButton((int)DialogButtonType.Positive)!;
        delete.FilterTouchesWhenObscured = true;
        delete.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(password.Text)) { error.Text = "Enter your password."; return; }
            delete.Enabled = false;
            try
            {
                await Night.Api.DeleteAccountAsync(password.Text!);
                dialog.Dismiss();
                await SignOutAsync();
                Ui.Toast(this, "Your account was deleted.");
            }
            catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
            {
                error.Text = ex.Message;
                delete.Enabled = true;
            }
        };
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
        _updateBox = Ui.Column(this, 0);
        page.AddView(_updateBox);
        if (CrashLog.Last is { } crash) page.AddView(CrashCard(crash));
        FillUpdate();
        switch (screen)
        {
            case Screen.Loading:
                page.AddView(Ui.Text(this, "Signing in…", 15, Palette.TextQuiet));
                break;
            case Screen.SignIn:
                BuildSignIn(page);
                break;
            case Screen.SignUp:
                BuildSignUp(page);
                break;
            case Screen.Code:
                BuildCode(page);
                break;
            case Screen.Home:
                BuildHome(page);
                break;
            case Screen.Settings:
                BuildSettings(page);
                break;
        }
        var root = Ui.Scroll(this, page);
        // Another app's see-through window over the sign-in or code screen can't pass taps through to it (tapjacking).
        root.FilterTouchesWhenObscured = screen is Screen.SignIn or Screen.SignUp or Screen.Code;
        SetContentView(root);
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
        page.AddView(Ui.Text(this, $"Night-loop for Android {AppInfo.Version}", 14, Palette.TextQuiet));
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
            "Use your Night-loop account. One device can be signed in at a time, so signing in here signs Night-loop out on your PC.", 13, Palette.TextQuiet));
        page.AddView(Ui.Heading(this, "New to Night-loop?"));
        page.AddView(Ui.Button(this, "Create an account", () =>
        {
            _message = "";
            Show(Screen.SignUp);
        }, primary: false));
    }

    private const string TermsUrl = "https://github.com/Infinity7915/Night-loop/blob/main/terms-of-service.md";
    private const string PrivacyUrl = "https://github.com/Infinity7915/Night-loop/blob/main/privacy-policy.md";

    /// <summary>Create an account, like the PC app's sign-up: email, password twice, the terms, then the emailed code.</summary>
    private void BuildSignUp(LinearLayout page)
    {
        page.AddView(Ui.Heading(this, "Create an account"));
        var card = Ui.Card(this);
        var email = Ui.Input(this, "Email", _email, InputTypes.ClassText | InputTypes.TextVariationEmailAddress);
        var password = Ui.Input(this, $"Password (at least {AccountRules.PasswordMinLength} characters)", "", InputTypes.ClassText | InputTypes.TextVariationPassword);
        var confirm = Ui.Input(this, "Password again", "", InputTypes.ClassText | InputTypes.TextVariationPassword);
        card.AddView(email);
        card.AddView(password);
        card.AddView(confirm);
        var terms = new CheckBox(this) { Text = "I'm at least 13 and accept the Night-loop Terms of Service and Privacy Policy" };
        terms.SetTextColor(Palette.Text);
        card.AddView(terms);
        var links = Ui.Row(this);
        links.AddView(Ui.Button(this, "Terms of Service", () => OpenLink(TermsUrl), primary: false));
        links.AddView(Ui.Button(this, "Privacy Policy", () => OpenLink(PrivacyUrl), primary: false));
        card.AddView(links);
        card.AddView(Ui.Text(this, "Macros created and stored in Night-loop are assets of Night-loop and aren't owned by the user (Terms of Service, section 5).", 12, Palette.TextQuiet));
        Button? button = null;
        button = Ui.Button(this, "Create account", () =>
        {
            _email = email.Text?.Trim() ?? "";
            var pass = password.Text ?? "";
            if (!AccountRules.IsValidEmail(_email)) _message = "Enter a valid email address.";
            else if (AccountRules.PasswordProblem(pass, _email) is { } problem) _message = problem;
            else if (pass != confirm.Text) _message = "The passwords don't match.";
            else if (!terms.Checked) _message = "Accept the terms to create an account.";
            else
            {
                _ = SignUpAsync(_email, pass, button!);
                return;
            }
            Ui.Toast(this, _message); // keep what was typed
            _message = "";
        });
        card.AddView(button);
        page.AddView(card);
        AddMessage(page);
        page.AddView(Ui.Text(this, "We'll email you a 6-digit code to check it's your address. Your username is made for you; change it later in Night-loop on your PC.", 13, Palette.TextQuiet));
        page.AddView(Ui.Button(this, "I already have an account", () =>
        {
            _message = "";
            Show(Screen.SignIn);
        }, primary: false));
    }

    private void OpenLink(string url)
    {
        try { StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url))); }
        catch (ActivityNotFoundException) { Ui.Toast(this, url); }
    }

    private async Task SignUpAsync(string email, string password, Button button)
    {
        button.Enabled = false;
        try
        {
            var challenge = await Night.Session.SignUpAsync(email, password, acceptedTerms: true);
            RunOnUiThread(() =>
            {
                _message = "";
                _unlocking = false;
                _signingUp = true;
                _challenge = challenge;
                _codeEmail = email;
                Show(Screen.Code);
            });
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() =>
            {
                _message = ex.Message;
                Show(Screen.SignUp);
            });
        }
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
        if (!_unlocking && !_signingUp) card.AddView(trust);
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
            Show(_signingUp ? Screen.SignUp : Screen.SignIn);
        }, primary: false));
    }

    private void BuildHome(LinearLayout page)
    {
        var user = Night.Session.User;
        var account = Ui.Row(this);
        account.AddView(Ui.Fill(Ui.Text(this, user is null ? "" : $"{user.Username} · {user.Email}", 14, Palette.TextQuiet)));
        account.AddView(Ui.Button(this, "⚙ Settings", () => Show(Screen.Settings), primary: false));
        page.AddView(account);

        _steps = Ui.Column(this, 0);
        page.AddView(_steps);

        page.AddView(Ui.Heading(this, "Your macros"));
        var actions = Ui.Row(this);
        actions.AddView(Ui.Button(this, "+ New macro", NewMacro));
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

    // ---- Settings ----

    private void BuildSettings(LinearLayout page)
    {
        var top = Ui.Row(this);
        top.AddView(Ui.Button(this, "← Back", () => Show(Screen.Home), primary: false));
        top.AddView(Ui.Fill(Ui.Text(this, "Settings", 20, Palette.Text, bold: true)));
        page.AddView(top);

        page.AddView(Ui.Heading(this, "Theme"));
        var themes = Ui.Card(this);
        themes.AddView(Ui.Text(this, "The same themes as Night-loop on PC. Kept on this phone.", 13, Palette.TextQuiet));
        foreach (var theme in Palette.Themes)
        {
            var row = Ui.Row(this);
            row.SetPadding(0, Palette.Dp(this, 8), 0, Palette.Dp(this, 8));
            var swatch = new View(this)
            {
                Background = Ui.Rounded(this, global::Android.Graphics.Color.ParseColor(theme.Background), 8,
                    global::Android.Graphics.Color.ParseColor(theme.Accent)),
            };
            row.AddView(swatch, new LinearLayout.LayoutParams(Palette.Dp(this, 28), Palette.Dp(this, 28)));
            var dot = new View(this) { Background = Ui.Rounded(this, global::Android.Graphics.Color.ParseColor(theme.Accent), 6) };
            var dotLayout = new LinearLayout.LayoutParams(Palette.Dp(this, 12), Palette.Dp(this, 12));
            dotLayout.SetMargins(Palette.Dp(this, 8), 0, Palette.Dp(this, 12), 0);
            row.AddView(dot, dotLayout);
            var current = theme.Id == Palette.Current.Id;
            row.AddView(Ui.Fill(Ui.Text(this, current ? $"{theme.Name}  ✓" : theme.Name, 16, current ? Palette.Accent : Palette.Text, bold: current)));
            row.Click += (_, _) =>
            {
                LocalSettings.Theme = theme.Id;
                Show(Screen.Settings);
            };
            themes.AddView(row);
        }
        page.AddView(themes);

        page.AddView(Ui.Heading(this, "Limits"));
        var limits = Ui.Card(this);
        limits.AddView(Ui.Text(this, "Loading…", 14, Palette.TextQuiet));
        page.AddView(limits);
        _ = LoadLimitsAsync(limits);

        page.AddView(Ui.Heading(this, "Playing"));
        var playing = Ui.Card(this);
        playing.AddView(Ui.Text(this, "Start delay (ms): how long Play waits before the first step, so the game is in front and the first tap lands.", 14, Palette.Text));
        var delay = Ui.Number(this, LocalSettings.StartDelayDefaultMs.ToString(), LocalSettings.StartDelayMs);
        delay.FocusChange += (_, e) =>
        {
            if (e.HasFocus) return;
            LocalSettings.StartDelayMs = int.TryParse(delay.Text, out var ms) ? ms : LocalSettings.StartDelayDefaultMs;
            delay.Text = LocalSettings.StartDelayMs.ToString();
        };
        playing.AddView(delay);
        playing.AddView(Ui.Text(this, $"0 – {LocalSettings.StartDelayMaxMs} ms. Default {LocalSettings.StartDelayDefaultMs}.", 12, Palette.TextQuiet));
        AddWait(playing, "Wait after taps (ms): after every Tap, Long press and Swipe, unless the step sets its own.",
            () => LocalSettings.WaitAfterTapsMs, v => LocalSettings.WaitAfterTapsMs = v);
        AddWait(playing, "Wait after every block (ms): a pause after each step. Taps that wait 0 skip it.",
            () => LocalSettings.WaitAfterBlocksMs, v => LocalSettings.WaitAfterBlocksMs = v);
        var follow = new global::Android.Widget.Switch(this) { Text = "Follow the playing step: with the ☰ step list open, the step being played is highlighted and kept in the middle of the list.", Checked = LocalSettings.FollowPlayingStep };
        follow.SetTextColor(Palette.Text);
        follow.SetPadding(0, Palette.Dp(this, 8), 0, Palette.Dp(this, 8));
        follow.CheckedChange += (_, e) => LocalSettings.FollowPlayingStep = e.IsChecked;
        playing.AddView(follow);
        var seasonal = new global::Android.Widget.Switch(this) { Text = "Seasonal themes: around holidays (Christmas week, Halloween, Valentine's and more) Night-loop's icon and bubble dress up for the season. Holidays follow your phone's region setting (for example Lunar New Year and Mid-Autumn in China, the 4th of July in the US). Your location isn't used or saved.", Checked = LocalSettings.SeasonalThemes };
        seasonal.SetTextColor(Palette.Text);
        seasonal.SetPadding(0, Palette.Dp(this, 8), 0, Palette.Dp(this, 8));
        var global = new global::Android.Widget.Switch(this) { Text = "Global themes: every country's holidays show when it's their time, not only your region's. When two overlap, the one whose main day is closer wins; within 3 days of each other it's a 50/50 pick for the day.", Checked = LocalSettings.GlobalSeasonalThemes };
        global.SetTextColor(Palette.Text);
        global.SetPadding(0, Palette.Dp(this, 8), 0, Palette.Dp(this, 8));
        // Only one of the two can be on (or neither).
        seasonal.CheckedChange += (_, e) =>
        {
            LocalSettings.SeasonalThemes = e.IsChecked;
            if (e.IsChecked && global.Checked) global.Checked = false;
            Seasons.ApplyLauncherIcon(this);
        };
        global.CheckedChange += (_, e) =>
        {
            LocalSettings.GlobalSeasonalThemes = e.IsChecked;
            if (e.IsChecked && seasonal.Checked) seasonal.Checked = false;
            Seasons.ApplyLauncherIcon(this);
        };
        playing.AddView(seasonal);
        playing.AddView(global);
        page.AddView(playing);

        page.AddView(Ui.Heading(this, "Account"));
        var account = Ui.Card(this);
        var user = Night.Session.User;
        if (user is not null)
        {
            account.AddView(Ui.Text(this, user.Username, 16, Palette.Text, bold: true));
            account.AddView(Ui.Text(this, user.Email, 14, Palette.TextQuiet));
        }
        account.AddView(Ui.Text(this, "One device can be signed in at a time. Change your name, email or password in Night-loop on your PC.", 13, Palette.TextQuiet));
        var buttons = Ui.Row(this);
        buttons.AddView(Ui.Button(this, "Download my data", () => _ = DownloadDataAsync(), primary: false));
        buttons.AddView(Ui.Button(this, "Sign out", () => _ = SignOutAsync(), primary: false));
        account.AddView(buttons);
        account.AddView(Ui.Button(this, "Delete account", AskDeleteAccount, primary: false));
        page.AddView(account);

        page.AddView(Ui.Heading(this, "Permissions"));
        var permissions = Ui.Card(this);
        foreach (var step in Permissions.Steps(this))
        {
            var row = Ui.Row(this);
            row.AddView(Ui.Fill(Ui.Text(this, step.Title, 15, Palette.Text)));
            row.AddView(Ui.Text(this, step.Done ? "On" : "Off", 14, step.Done ? Palette.Good : Palette.Warn, bold: true));
            row.AddView(Ui.Button(this, "Open", () => step.Open(this), primary: false));
            permissions.AddView(row);
            if (step.HelpLink is not null) permissions.AddView(HelpLink(step));
        }
        page.AddView(permissions);

        page.AddView(Ui.Heading(this, "About"));
        var about = Ui.Card(this);
        about.AddView(Ui.Text(this, $"Night-loop for Android {AppInfo.Version}", 14, Palette.Text));
        about.AddView(Ui.Button(this, "Check for updates", () =>
        {
            if (_updateStatus is not null) _updateStatus.Text = "Checking…";
            _ = CheckForUpdateAsync(quiet: false);
        }, primary: false));
        _updateStatus = Ui.Text(this, "", 13, Palette.TextQuiet);
        about.AddView(_updateStatus);
        page.AddView(about);
    }

    private async Task LoadLimitsAsync(LinearLayout card)
    {
        try
        {
            var u = await Night.Api.GetLimitsAsync();
            RunOnUiThread(() =>
            {
                card.RemoveAllViews();
                Line("Macros", $"{u.Macros} of {u.MacrosMax}");
                Line("Blocks", $"{u.Blocks:N0} of {u.BlocksMax:N0}");
                Line("New macros today", $"{u.NewMacrosToday} of {u.NewMacrosPerDay}");
                Line("Blocks placed today", $"{u.BlocksToday} of {u.BlocksPerDay}");
                card.AddView(Ui.Text(this, $"Daily counts reset at {u.ResetsAt.ToLocalTime():HH:mm} (midnight UTC).", 13, Palette.TextQuiet));
            });

            void Line(string name, string value)
            {
                var row = Ui.Row(this);
                row.AddView(Ui.Fill(Ui.Text(this, name, 15, Palette.Text)));
                row.AddView(Ui.Text(this, value, 15, Palette.Accent, bold: true));
                card.AddView(row);
            }
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() =>
            {
                card.RemoveAllViews();
                card.AddView(Ui.Text(this, ex.Message, 14, Palette.Bad));
            });
        }
    }

    /// <summary>A text summary of the account (like the PC app's Download my data), shared to wherever you pick.</summary>
    private async Task DownloadDataAsync()
    {
        try
        {
            var d = await Night.Api.GetAccountDataAsync();
            var text =
                $"Night-loop account data\n" +
                $"Exported: {d.ExportedAt.ToLocalTime():yyyy-MM-dd HH:mm}\n\n" +
                $"Username: {d.Username}\nEmail: {d.Email}\nUser number: {d.UserNumber}\n" +
                $"Created: {d.CreatedAt.ToLocalTime():yyyy-MM-dd}\n" +
                $"Password: {(d.PasswordSet ? "set (hidden)" : "not set")}\n" +
                $"Time used: {TimeSpan.FromSeconds(d.UsageSeconds):%d'd 'hh'h 'mm'm'}\n" +
                $"Macros: {d.MacroCount}\n";
            RunOnUiThread(() =>
            {
                var share = new Intent(Intent.ActionSend).SetType("text/plain")!
                    .PutExtra(Intent.ExtraSubject, "Night-loop account data")!
                    .PutExtra(Intent.ExtraText, text)!;
                StartActivity(Intent.CreateChooser(share, "Save or send your data"));
            });
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            RunOnUiThread(() => Ui.Toast(this, ex.Message));
        }
    }

#pragma warning disable CS0672, CA1422 // Back on older Androids; newer ones route it here too for plain activities.
    public override void OnBackPressed()
    {
        if (_screen == Screen.Settings)
        {
            Show(Screen.Home);
            return;
        }
        base.OnBackPressed();
    }
#pragma warning restore CS0672, CA1422

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
            if (step.HelpLink is not null) card.AddView(HelpLink(step));
            card.AddView(Ui.Button(this, "Turn on", () => step.Open(this)));
            _steps.AddView(card);
        }

        if (_bubbleButton is null || _bubbleNote is null) return;
        var canDraw = Permissions.CanDrawOverlays(this);
        _bubbleButton.Enabled = canDraw;
        _bubbleButton.Text = BubbleService.Running ? "Hide bubble" : "Show bubble";
        _bubbleNote.Text = canDraw
            ? "Make and play macros from the bubble over your game: tap it, pick a macro to show its numbered dots, or + New macro. Drag the bubble anywhere."
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
            _macroList.AddView(Ui.Text(this, "No macros yet. Tap + New macro to make one.", 14, Palette.TextQuiet));
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
                var name = macro.Name;
                row.AddView(Ui.Small(this, "🗑", () => AskDeleteMacro(this, id, name), $"Delete {macro.Name}"));
                card.Click += (_, _) => OpenEditor(id);
                card.LongClick += (_, _) => AskDeleteMacro(this, id, name);
            }
            card.AddView(row);
            _macroList.AddView(card);
        }
        if (Night.MacroLimit > 0)
            _macroList.AddView(Ui.Text(this, $"{Night.Macros.Count} / {Night.MacroLimit} macros used", 13, Palette.TextQuiet));
    }

    /// <summary>Asks first, then deletes the macro from the account (stops it if it's playing).</summary>
    internal static void AskDeleteMacro(Activity a, Guid id, string name, Action? deleted = null)
    {
        Dialogs.Builder(a)
            .SetTitle("Delete macro?")!
            .SetMessage($"\"{name}\" will be deleted for good. This can't be undone.")!
            .SetPositiveButton("Delete", (_, _) => _ = DeleteMacroAsync(a, id, name, deleted))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private static async Task DeleteMacroAsync(Activity a, Guid id, string name, Action? deleted)
    {
        try
        {
            if (Night.IsRunning(id)) Night.Stop(id);
            await Night.Api.DeleteMacroAsync(id);
            await Night.RefreshMacrosAsync();
            a.RunOnUiThread(() =>
            {
                Ui.Toast(a, $"Deleted \"{name}\".");
                deleted?.Invoke();
            });
        }
        catch (Exception ex) when (ex is ApiException or ServerUnreachableException)
        {
            a.RunOnUiThread(() => Ui.Toast(a, ex.Message));
        }
    }

    private async Task PlayAsync(Guid id)
    {
        // Out of the way first, so the macro taps the app underneath (it starts a moment later).
        MoveTaskToBack(true);
        var error = await Night.PlayAsync(id);
        RunOnUiThread(() => Ui.Toast(this, error ?? (BubbleService.Running ? "Playing. Use the bubble to stop it." : "Playing. Open Night-loop to stop it.")));
    }

    private void NewMacro()
    {
        var name = Ui.Input(this, "Macro name", "New macro");
        var box = Ui.Column(this, Palette.Dp(this, 20));
        box.AddView(name);
        Dialogs.Builder(this)
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

    private void AddWait(LinearLayout card, string label, Func<int> get, Action<int> set)
    {
        card.AddView(Ui.Text(this, label, 14, Palette.Text));
        var box = Ui.Number(this, "0", get());
        box.FocusChange += (_, e) =>
        {
            if (e.HasFocus) return;
            set(int.TryParse(box.Text, out var ms) ? ms : 0);
            box.Text = get().ToString();
        };
        card.AddView(box);
        card.AddView(Ui.Text(this, $"0 – {LocalSettings.WaitAfterMaxMs} ms. Default 0.", 12, Palette.TextQuiet));
    }

    /// <summary>Link-blue text under a step that opens its tutorial on GitHub.</summary>
    private TextView HelpLink(SetupStep step)
    {
        var link = Ui.Text(this, step.HelpText ?? "Watch how", 13, Android.Graphics.Color.ParseColor("#4EA1FF"));
        link.PaintFlags |= Android.Graphics.PaintFlags.UnderlineText;
        link.Clickable = true;
        link.Click += (_, _) => StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(step.HelpLink)));
        return link;
    }

    /// <summary>Shown after Night-loop closed unexpectedly: copy the report for a bug report, or dismiss it.</summary>
    private View CrashCard(string crash)
    {
        var card = Ui.Card(this);
        card.AddView(Ui.Text(this, "Night-loop closed unexpectedly last time", 16, Palette.Warn, bold: true));
        card.AddView(Ui.Text(this, "Copy the report and send it to us (Discord or GitHub) so we can fix it. It only has the error, your Night-loop version and your phone model.", 13, Palette.TextQuiet));
        var row = Ui.Row(this);
        row.AddView(Ui.Button(this, "Copy report", () =>
        {
            var clip = (global::Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
            clip.PrimaryClip = ClipData.NewPlainText("Night-loop crash", crash);
            Ui.Toast(this, "Copied.");
        }));
        row.AddView(Ui.Button(this, "Dismiss", () => { CrashLog.Clear(); Show(_screen); }, primary: false));
        card.AddView(row);
        return card;
    }
}
