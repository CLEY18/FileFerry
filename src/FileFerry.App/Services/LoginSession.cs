using FileFerry.Core.Ports;

namespace FileFerry.App.Services;

/// <summary>
/// Holder of the active challenge between the welcome screen and the WebView page: Shell parameters are strings
/// and would end up in the URI, while <see cref="LoginChallenge.PollToken"/> is a secret
///so it is passed only in memory inside one DI scope.
/// </summary>
public sealed class LoginSession
{
    /// <summary>
    /// The challenge started on the welcome screen; <see langword="null"/> outside an active flow.
    /// </summary>
    public LoginChallenge? Challenge { get; set; }
}
