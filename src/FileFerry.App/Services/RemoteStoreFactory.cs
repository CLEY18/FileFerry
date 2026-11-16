using FileFerry.Client;
using FileFerry.Client.Retry;
using FileFerry.Client.WebDav;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.App.Services;

/// <summary>
/// Factory of remote stores from a saved <see cref="Account"/>: builds the user's DAV root and
/// fetches the "live" token from <see cref="ISecretStore"/> on every request —
/// the secret is not cached in the UI layer. The wizard only needs an <see cref="IRemoteBrowser"/>.
/// </summary>
public sealed class RemoteStoreFactory
{
    private readonly ISecretStore _secrets;

    /// <summary>
    /// Creates the factory over the secret store.
    /// </summary>
    public RemoteStoreFactory(ISecretStore secrets) => _secrets = secrets;

    /// <summary>
    /// The account's DAV files root: <c>{BaseUrl}/remote.php/dav/files/{user}/</c>.
    /// </summary>
    public static Uri DavFilesRoot(Account account) =>
        new(new Uri(account.BaseUrl), $"/remote.php/dav/files/{Uri.EscapeDataString(account.UserName)}/");

    /// <summary>
    /// Build a browser for the account. The <see cref="HttpClient"/> lives together with the
    /// browser — the wizard holds one instance per session (transient VM), with connection pooling
    /// within the run.
    /// </summary>
    public IRemoteBrowser CreateBrowser(Account account)
    {
        var http = DavHttpClient.Create(DavFilesRoot(account));
        return new WebDavRemoteStore(http, account.UserName, ct => _secrets.GetAsync(account.TokenRef, ct));
    }

    /// <summary>
    /// The account's full write side for SyncRun: WebDAV + retry decorator (retry policy
    /// <see cref="RetryPolicyOptions"/> from Client); the token stays "live" from <see cref="ISecretStore"/>.
    /// </summary>
    public IWritableRemoteStore CreateStore(Account account)
    {
        var http = DavHttpClient.Create(DavFilesRoot(account));
        var store = new WebDavRemoteStore(http, account.UserName, ct => _secrets.GetAsync(account.TokenRef, ct));
        return new RetryingRemoteStore(store, new RetryPolicyOptions(), new TaskDelay());
    }

    /// <summary>
    /// The "server is alive" probe: <c>status.php</c> lives at the server root, so the base is not the DAV path.
    /// </summary>
    public IAvailabilityProbe CreateProbe(Account account) =>
        new WebDavAvailabilityProbe(DavHttpClient.Create(
            new Uri(new Uri(account.BaseUrl), "/"), TimeSpan.FromSeconds(10)));
}
