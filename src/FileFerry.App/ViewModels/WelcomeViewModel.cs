using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Ports;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the welcome screen: address input → Login Flow v2 start → WebView screen; a DNS-like
/// error suggests entering the IP manually. All state and logic live here — the page's
/// code-behind only binds.
/// </summary>
public sealed class WelcomeViewModel : Mvvm.ObservableObject
{
    private readonly ILoginFlow _loginFlow;
    private readonly LoginSession _session;
    private readonly INavigationService _navigation;
    private readonly IAppDialogs _dialogs;
    private string _host = string.Empty;
    private bool _isBusy;
    private bool _showManualIp;
    private bool _isEntryFocused;
    private string? _errorText;

    public WelcomeViewModel(ILoginFlow loginFlow, LoginSession session, INavigationService navigation, IAppDialogs dialogs)
    {
        _loginFlow = loginFlow;
        _session = session;
        _navigation = navigation;
        _dialogs = dialogs;
        ConnectCommand = new Mvvm.AsyncRelayCommand(ConnectAsync, () => !IsBusy);
        UseManualIpCommand = new Mvvm.AsyncRelayCommand(ct =>
        {
            // The input is always visible: "manually" = hide the hint and focus the field.
            ErrorText = null;
            ShowManualIp = false;
            IsEntryFocused = true;
            return Task.CompletedTask;
        });
        HelpCommand = new Mvvm.AsyncRelayCommand(ct => _dialogs.ShowAlertAsync(
            AppStrings.HelpTitle, AppStrings.HelpBody, AppStrings.HelpOk, ct));
    }

    /// <summary>
    /// Raw server address from the input (normalized by <see cref="ServerUrl"/>).
    /// </summary>
    public string Host
    {
        get => _host;
        set => Set(ref _host, value);
    }

    /// <summary>
    /// The screen is busy initializing the flow — controls are blocked.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                ConnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Focus on the address field (after "enter IP manually").
    /// </summary>
    public bool IsEntryFocused
    {
        get => _isEntryFocused;
        set => Set(ref _isEntryFocused, value);
    }

    /// <summary>
    /// Show the "enter IP manually" hint after a DNS-like failure on the welcome screen.
    /// </summary>
    public bool ShowManualIp
    {
        get => _showManualIp;
        set => Set(ref _showManualIp, value);
    }

    /// <summary>
    /// Error line (inline, StatusError); null — no errors.
    /// </summary>
    public string? ErrorText
    {
        get => _errorText;
        set => Set(ref _errorText, value);
    }

    /// <summary>
    /// "Connect": address normalization → flow start → navigate to the WebView screen.
    /// </summary>
    public Mvvm.AsyncRelayCommand ConnectCommand { get; }

    /// <summary>
    /// "Enter IP manually": focuses the address field (the hint shown after a DNS failure).
    /// </summary>
    public Mvvm.AsyncRelayCommand UseManualIpCommand { get; }

    /// <summary>
    /// Help about DNS/Private DNS/IP (the dialog).
    /// </summary>
    public Mvvm.AsyncRelayCommand HelpCommand { get; }

    private async Task ConnectAsync(CancellationToken ct)
    {
        ErrorText = null;
        ShowManualIp = false;
        if (!ServerUrl.TryNormalize(Host, out var baseUrl))
        {
            ErrorText = AppStrings.ErrorHostInvalid;
            return;
        }

        IsBusy = true;
        try
        {
            var attempt = await ConnectWithConsentAsync(baseUrl, ServerUrl.HasExplicitScheme(Host), ct);
            if (attempt is not { } started)
            {
                return; // the user declined an unencrypted connection — stay on the field, no error
            }

            // The Client runs with ConfigureAwait(false) — continuations touch Shell only via the UI dispatcher.
            await Application.Current!.Dispatcher.DispatchAsync(() =>
            {
                if (started.IsSuccess)
                {
                    _session.Challenge = started.Value;
                    return _navigation.GoAsync(AppShell.LoginRoute, ct);
                }

                ErrorText = Services.ErrorUi.Describe(started.Error);
                ShowManualIp = started.Error.Kind is Core.Domain.ErrorKind.ServerGone or Core.Domain.ErrorKind.Network;
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            // The user left the welcome screen — the state is no longer relevant.
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() =>
            {
                IsBusy = false;
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// Flow start under the scheme rule: https runs silently; cleartext runs only on the
    /// user's explicit yes — typed in full, or accepted after the encrypted attempt failed
    /// on a bare address (a LAN server without TLS is common, and a dead field helps nobody).
    /// Null — the user declined cleartext and nothing was attempted with it.
    /// </summary>
    private async Task<Core.Domain.Result<Core.Ports.LoginChallenge, Core.Domain.SyncError>?> ConnectWithConsentAsync(
        Uri baseUrl, bool explicitScheme, CancellationToken ct)
    {
        if (baseUrl.Scheme == Uri.UriSchemeHttp
            && !await ConfirmCleartextAsync(AppStrings.WelcomeCleartextBody, ct))
        {
            return null;
        }

        var started = await _loginFlow.StartAsync(baseUrl, ct);
        if (started.IsFailure
            && baseUrl.Scheme == Uri.UriSchemeHttps
            && !explicitScheme
            && started.Error.Kind is Core.Domain.ErrorKind.Network or Core.Domain.ErrorKind.ServerGone
            && await ConfirmCleartextAsync(AppStrings.WelcomeHttpsFailedBody, ct))
        {
            return await _loginFlow.StartAsync(ServerUrl.WithScheme(baseUrl, Uri.UriSchemeHttp), ct);
        }

        return started;
    }

    /// <summary>
    /// The one cleartext warning (its body names the occasion); true — connect unencrypted.
    /// </summary>
    private Task<bool> ConfirmCleartextAsync(string body, CancellationToken ct) =>
        _dialogs.ConfirmAsync(
            AppStrings.WelcomeCleartextTitle, body, AppStrings.WelcomeCleartextAccept, AppStrings.DialogCancel, ct);
}
