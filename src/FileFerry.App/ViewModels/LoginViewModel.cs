using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;
using FileFerry.Data.Sqlite;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the WebView screen of Login Flow v2 (welcome screen): shows <c>LoginUrl</c> and polls
/// the poll endpoint in the background; on consent — saves the account (DB + Keystore) and
/// returns to the Pair List. Secrets (poll token, app password) never touch UI properties.
/// </summary>
public sealed class LoginViewModel : Mvvm.ObservableObject
{
    private readonly ILoginFlow _loginFlow;
    private readonly LoginSession _session;
    private readonly IAccountRepository _accounts;
    private readonly ISecretStore _secrets;
    private readonly DatabaseInitializer _database;
    private readonly INavigationService _navigation;
    private string _title = AppStrings.LoginTitle;
    private string _loginUrl = string.Empty;
    private bool _isWaiting;
    private string? _errorText;
    private Mvvm.AsyncRelayCommand? _pollCommand;

    public LoginViewModel(
        ILoginFlow loginFlow,
        LoginSession session,
        IAccountRepository accounts,
        ISecretStore secrets,
        DatabaseInitializer database,
        INavigationService navigation)
    {
        _loginFlow = loginFlow;
        _session = session;
        _accounts = accounts;
        _secrets = secrets;
        _database = database;
        _navigation = navigation;
        CloseCommand = new Mvvm.AsyncRelayCommand(ct =>
        {
            _pollCommand?.Cancel();
            return _navigation.BackAsync(ct);
        });
    }

    /// <summary>
    /// TopAppBar title ("Sign in to &lt;host&gt;").
    /// </summary>
    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    /// <summary>
    /// Address for the WebView; empty — the screen is preparing or finished.
    /// </summary>
    public string LoginUrl
    {
        get => _loginUrl;
        private set => Set(ref _loginUrl, value);
    }

    /// <summary>
    /// Consent is being awaited (poll) — show the indicator.
    /// </summary>
    public bool IsWaiting
    {
        get => _isWaiting;
        private set => Set(ref _isWaiting, value);
    }

    /// <summary>
    /// Inline error text; null — all good.
    /// </summary>
    public string? ErrorText
    {
        get => _errorText;
        private set => Set(ref _errorText, value);
    }

    /// <summary>
    /// Close: cancels the poll and returns to the welcome screen.
    /// </summary>
    public Mvvm.AsyncRelayCommand CloseCommand { get; }

    /// <summary>
    /// Starts the flow: prepares the WebView and launches background polling. Called once
    /// from code-behind in <c>OnAppearing</c> — the logic itself stays in the VM.
    /// </summary>
    public void Start()
    {
        var challenge = _session.Challenge;
        if (challenge is null)
        {
            ErrorText = AppStrings.ErrorSessionExpired;
            return;
        }

        Title = string.Format(AppStrings.LoginTitleFormat, challenge.ServerName);
        LoginUrl = challenge.LoginUrl.ToString();
        _pollCommand = new Mvvm.AsyncRelayCommand(ct => PollAsync(challenge, ct));
        _pollCommand.Execute(null);
    }

    private async Task PollAsync(LoginChallenge challenge, CancellationToken ct)
    {
        IsWaiting = true;
        try
        {
            var done = await _loginFlow.CompleteAsync(challenge, ct);
            if (done.IsSuccess)
            {
                await PersistAsync(done.Value, ct); // IO off the UI thread
            }

            // The continuation after ConfigureAwait(false) inside Client — Shell/properties only from the UI thread.
            await Application.Current!.Dispatcher.DispatchAsync(async () =>
            {
                if (done.IsSuccess)
                {
                    // The account exists → reset the whole welcome/login stack onto the main pair-list tab;
                    // ".." here would leave the user stuck on the welcome screen until the next app restart.
                    _session.Challenge = null;
                    await _navigation.GoAsync(AppShell.PairListRoute, CancellationToken.None);
                }
                else
                {
                    ErrorText = Services.ErrorUi.Describe(done.Error);
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Closed by the user — no error.
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() =>
            {
                IsWaiting = false;
                return Task.CompletedTask;
            });
        }
    }

    private async Task PersistAsync(LoginCredentials credentials, CancellationToken ct)
    {
        await _database.EnsureReadyAsync();
        var account = new Account(Guid.NewGuid(), credentials.ServerBaseUrl.ToString(), credentials.LoginName, string.Empty);
        var tokenRef = $"account/{account.Id:N}/token";
        await _secrets.SetAsync(tokenRef, credentials.AppPassword, ct);
        await _accounts.SaveAsync(account with { TokenRef = tokenRef }, ct);
    }
}
