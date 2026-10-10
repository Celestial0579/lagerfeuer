using Lagerfeuer.Models;
using Lagerfeuer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Lagerfeuer.Views;

public sealed record LoginPageArgs(string? Message, bool CanRetry);

public sealed partial class LoginPage : Page
{
    private Uri? _server;
    private ServerStatus? _status;

    public LoginPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var args = e.Parameter as LoginPageArgs;
        ServerBox.Text = App.Settings.ServerUrl?.TrimEnd('/') ?? "";
        UserBox.Text = App.Settings.Username ?? "";

        if (args?.Message is { } msg) ShowMessage(msg, InfoBarSeverity.Warning, args.CanRetry);

        // Bekannter Server → Anmeldemethoden direkt abfragen.
        if (!string.IsNullOrWhiteSpace(ServerBox.Text) && args?.CanRetry != true) _ = ConnectAsync();
    }

    private void ShowMessage(string message, InfoBarSeverity severity, bool canRetry = false)
    {
        MessageBar.Message = message;
        MessageBar.Severity = severity;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        MessageBar.IsOpen = true;
    }

    private void SetBusy(bool busy)
    {
        Busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.IsEnabled = LoginButton.IsEnabled = SsoButton.IsEnabled = ServerBox.IsEnabled = !busy;
    }

    private void OnRetry(object sender, RoutedEventArgs e)
    {
        MessageBar.IsOpen = false;
        App.MainWindow.RetryStart();
    }

    private void OnServerTextChanged(object sender, TextChangedEventArgs e)
    {
        // Andere Adresse → Schritt 2 zurücksetzen
        try
        {
            if (_server is not null && AbsApiClient.NormalizeServerUrl(ServerBox.Text) == _server) return;
        }
        catch (AbsApiException) { /* ungültig → zurücksetzen */ }
        AuthPanel.Visibility = Visibility.Collapsed;
        ConnectButton.Visibility = Visibility.Visible;
        _status = null;
    }

    private void OnServerKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) _ = ConnectAsync();
    }

    private void OnPasswordKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) _ = LocalLoginAsync();
    }

    private void OnConnect(object sender, RoutedEventArgs e) => _ = ConnectAsync();
    private void OnLocalLogin(object sender, RoutedEventArgs e) => _ = LocalLoginAsync();
    private void OnSsoLogin(object sender, RoutedEventArgs e) => _ = SsoLoginAsync();

    private async Task ConnectAsync()
    {
        MessageBar.IsOpen = false;
        SetBusy(true);
        try
        {
            _server = AbsApiClient.NormalizeServerUrl(ServerBox.Text);
            _status = await App.Api.GetStatusAsync(_server);
            if (!_status.IsInit)
                throw new AbsApiException("Der Server ist noch nicht eingerichtet. Bitte zuerst im Browser einrichten.");

            ServerInfoText.Text = $"{_server.Host} · Audiobookshelf {_status.ServerVersion}";
            SsoButton.Visibility = _status.SupportsOpenId ? Visibility.Visible : Visibility.Collapsed;
            SsoButtonText.Text = string.IsNullOrWhiteSpace(_status.AuthFormData?.AuthOpenIDButtonText)
                ? "Mit SSO anmelden"
                : _status.AuthFormData!.AuthOpenIDButtonText!;
            LocalPanel.Visibility = _status.SupportsLocal ? Visibility.Visible : Visibility.Collapsed;
            OrSeparator.Visibility = _status.SupportsOpenId ? Visibility.Visible : Visibility.Collapsed;

            ConnectButton.Visibility = Visibility.Collapsed;
            AuthPanel.Visibility = Visibility.Visible;

            if (_status.SupportsLocal)
            {
                if (string.IsNullOrEmpty(UserBox.Text)) UserBox.Focus(FocusState.Programmatic);
                else PasswordBox.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task LocalLoginAsync()
    {
        if (_server is null) return;
        if (string.IsNullOrWhiteSpace(UserBox.Text))
        {
            ShowMessage("Bitte Benutzernamen eingeben.", InfoBarSeverity.Warning);
            return;
        }

        MessageBar.IsOpen = false;
        SetBusy(true);
        try
        {
            var login = await App.Api.LoginAsync(_server, UserBox.Text.Trim(), PasswordBox.Password);
            PasswordBox.Password = "";
            await App.MainWindow.OnLoggedInAsync(login);
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private CancellationTokenSource? _ssoCts;

    private void OnReopenBrowser(object sender, RoutedEventArgs e) => OpenIdLogin.ReopenBrowser();
    private void OnCancelSso(object sender, RoutedEventArgs e) => _ssoCts?.Cancel();

    private async Task SsoLoginAsync()
    {
        if (_server is null) return;
        MessageBar.IsOpen = false;
        SetBusy(true);
        using var cts = _ssoCts = new CancellationTokenSource();
        SsoWaitBar.IsOpen = true;
        try
        {
            var login = await OpenIdLogin.LoginAsync(App.Api, _server, cts.Token);
            SsoWaitBar.IsOpen = false;
            if (login is null) return; // abgebrochen
            await App.MainWindow.OnLoggedInAsync(login);
        }
        catch (Exception ex)
        {
            Log.Error("SSO-Anmeldung fehlgeschlagen", ex);
            ShowMessage(ex.Message + "\n\nHinweis: In Audiobookshelf muss unter Einstellungen → Authentifizierung " +
                        $"„{OpenIdLogin.RedirectUri}“ als erlaubte Mobile-Redirect-URI eingetragen sein.",
                InfoBarSeverity.Error);
        }
        finally
        {
            _ssoCts = null;
            SsoWaitBar.IsOpen = false;
            SetBusy(false);
        }
    }
}
