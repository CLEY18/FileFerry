using FileFerry.Core.Domain;

namespace FileFerry.Core.Ports;

/// <summary>
/// "Initialization" step of Login Flow v2 (Nextcloud): the server issued a user link
/// and a token+endpoint pair for polling. <see cref="PollToken"/> is a secret: never into
/// logs or messages (invariant); <see cref="ToString"/> is masked.
/// </summary>
/// <param name="ServerBaseUrl">The base URL the user entered — the account's canonical server address;
/// a server response never rewrites it, it only travels back inside the challenge.</param>
/// <param name="LoginUrl">URL shown to the user (WebView / QR code); same-origin as <see cref="ServerBaseUrl"/>.</param>
/// <param name="PollEndpoint">URL to POST-poll for consent; same-origin as <see cref="ServerBaseUrl"/>.</param>
/// <param name="PollToken">Poll token (secret).</param>
/// <param name="ServerName">Server name from the response ("Home", "Cloud @ home") — for the UI.</param>
/// <param name="ServerVersion">Server version from the response — for diagnostics and the account row.</param>
public sealed record LoginChallenge(Uri ServerBaseUrl, Uri LoginUrl, Uri PollEndpoint, string PollToken, string ServerName, string ServerVersion)
{
    /// <summary>
    /// Masked output: no token even in an accidental <c>{challenge}</c> in a log line.
    /// </summary>
    public override string ToString() => $"LoginChallenge({ServerName} v{ServerVersion}, poll ***)";
}

/// <summary>
/// Result of user consent: login + app password. <see cref="AppPassword"/> is a secret,
/// routed only into <see cref="ISecretStore"/>; <see cref="ToString"/> is masked.
/// </summary>
/// <param name="ServerBaseUrl">Server the flow started from (the account's canonical base URL).</param>
/// <param name="LoginName">User login (for displayName and DAV paths).</param>
/// <param name="AppPassword">App password (secret; the format may contain dashes/groups).</param>
/// <param name="ServerName">Server name from the final response.</param>
/// <param name="ServerVersion">Server version from the final response.</param>
public sealed record LoginCredentials(Uri ServerBaseUrl, string LoginName, string AppPassword, string ServerName, string ServerVersion)
{
    /// <summary>
    /// Masked output: no password even in a trace.
    /// </summary>
    public override string ToString() => $"LoginCredentials({LoginName}@{ServerBaseUrl.Host}, ***)";
}

/// <summary>
/// Poll status of user consent.
/// </summary>
public enum LoginPollStatus
{
    /// <summary>
    /// User has not confirmed yet — keep polling with pauses.
    /// </summary>
    Pending,

    /// <summary>
    /// Confirmed — <see cref="LoginPollOutcome.Credentials"/> is populated.
    /// </summary>
    Authorized,
}

/// <summary>
/// Reply of one poll: either "wait" or ready credentials (sum type).
/// </summary>
public sealed record LoginPollOutcome
{
    private LoginPollOutcome(LoginPollStatus status, LoginCredentials? credentials)
    {
        Status = status;
        Credentials = credentials;
    }

    /// <summary>
    /// "No consent yet" case — keep polling; <see cref="Credentials"/> stays null.
    /// </summary>
    public static LoginPollOutcome Pending { get; } = new(LoginPollStatus.Pending, null);

    /// <summary>
    /// Consent case factory — wraps the issued credentials (route them straight to <see cref="ISecretStore"/>).
    /// </summary>
    public static LoginPollOutcome Authorized(LoginCredentials credentials) => new(LoginPollStatus.Authorized, credentials);

    /// <summary>
    /// Sum-type discriminator; gates <see cref="Credentials"/>.
    /// </summary>
    public LoginPollStatus Status { get; }

    /// <summary>
    /// Credentials only when <see cref="Status"/> == <see cref="LoginPollStatus.Authorized"/>.
    /// </summary>
    public LoginCredentials? Credentials { get; }
}

/// <summary>
/// Login Flow v2 client (_2, DAV): <c>POST index.php/login/v2</c> → show
/// <see cref="LoginChallenge.LoginUrl"/> → poll until consent → app password.
/// The implementation is Client; the UI sees only this port (DIP).
/// </summary>
public interface ILoginFlow
{
    /// <summary>
    /// Initiate the flow at <paramref name="serverBaseUrl"/> (user's host, may include a subpath).
    /// </summary>
    /// <param name="serverBaseUrl">Server to start the flow at; kept later as the account's base URL.</param>
    /// <param name="ct">Cancellation (the user closed the login screen).</param>
    Task<Result<LoginChallenge, SyncError>> StartAsync(Uri serverBaseUrl, CancellationToken ct);

    /// <summary>
    /// One poll of the poll-endpoint; without consent — <see cref="LoginPollOutcome.Pending"/>.
    /// </summary>
    Task<Result<LoginPollOutcome, SyncError>> PollAsync(LoginChallenge challenge, CancellationToken ct);

    /// <summary>
    /// "Poll with pauses until consent/timeout" loop (the adapter owns the pause policy).
    /// </summary>
    Task<Result<LoginCredentials, SyncError>> CompleteAsync(LoginChallenge challenge, CancellationToken ct);
}
