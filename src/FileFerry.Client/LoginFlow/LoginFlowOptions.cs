namespace FileFerry.Client.LoginFlow;

/// <summary>
/// Login Flow v2 polling policy (Nextcloud "Login flow v2", poll section): the "not yet approved"
/// interval, the ceiling on waiting for user consent, and the User-Agent for requests.
/// </summary>
public sealed record LoginFlowOptions
{
    /// <summary>
    /// Defaults: 1 s between polls (as in official clients), 5 min wait.
    /// </summary>
    public static readonly LoginFlowOptions Defaults = new();

    /// <summary>
    /// Pause between polls of the poll endpoint while in the "awaiting consent" state.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Total consent wait — once exhausted, the flow is cancelled (the token would expire anyway).
    /// </summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Pause multiplier for a "too many requests" (429) response — back off politely.
    /// </summary>
    public double RateLimitBackoffFactor { get; init; } = 4;

    /// <summary>
    /// User-Agent for flow requests (servers prefer identified clients).
    /// </summary>
    public string UserAgent { get; init; } = "FileFerry";
}
