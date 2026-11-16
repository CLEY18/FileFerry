using FileFerry.Client.WebDav;
using FileFerry.ContractTests;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// Integration of <see cref="WebDavRemoteStore"/> against a live Nextcloud:
/// <c>NC_URL=… NC_USER=… NC_TOKEN=… dotnet test --filter Category=Integration</c>.
/// Each test runs in its own sandbox <c>FileFerryContract/&lt;guid&gt;/</c>; without the environment
/// variables every <see cref="IntegrationFactAttribute"/> test is honestly SKIPPED.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WebDavRemoteStoreIntegration
{
    private readonly Uri _baseAddress;
    private readonly string _user;
    private readonly string _token;

    /// <summary>
    /// Live-server configuration from the environment (the attributes guarantee the gates are set).
    /// </summary>
    public WebDavRemoteStoreIntegration()
    {
        var url = Environment.GetEnvironmentVariable("NC_URL") ?? "http://127.0.0.1";
        _token = Environment.GetEnvironmentVariable("NC_TOKEN") ?? string.Empty;
        _user = Environment.GetEnvironmentVariable("NC_USER") ?? "guest";

        // Unique sandbox per test instance: parallel Facts don't interfere (the token lives only here).
        var sandbox = $"FileFerryContract/{Guid.NewGuid():N}";
        _baseAddress = new Uri(new Uri(url.TrimEnd('/') + "/"), $"remote.php/dav/files/{_user}/{sandbox}/");
    }

    /// <summary>
    /// Store bound to this test's sandbox.
    /// </summary>
    private IWritableRemoteStore Create() =>
        new WebDavRemoteStore(
            new HttpClient { BaseAddress = _baseAddress, Timeout = TimeSpan.FromMinutes(2) },
            _user,
            _ => Task.FromResult<string?>(_token));

    /// <summary>
    /// Round-trip + ETag on the real server.
    /// </summary>
    [IntegrationFact]
    public Task Write_ThenRead_RoundTrips() => RemoteStoreContractSuite.WriteThenReadRoundTrips(Create);

    /// <summary>
    /// Overwriting changes the ETag.
    /// </summary>
    [IntegrationFact]
    public Task Write_Again_ChangesEtag() => RemoteStoreContractSuite.WriteAgainChangesEtag(Create);

    /// <summary>
    /// PROPFIND Depth-1 returns only direct children.
    /// </summary>
    [IntegrationFact]
    public Task List_Root_ReturnsDirectChildrenOnly() => RemoteStoreContractSuite.ListRootChildrenOnly(Create);

    /// <summary>
    /// Missing folder — NotFound.
    /// </summary>
    [IntegrationFact]
    public Task List_MissingFolder_NotFound() => RemoteStoreContractSuite.ListMissingFolderNotFound(Create);

    /// <summary>
    /// MKCOL is idempotent (405 ≡ success).
    /// </summary>
    [IntegrationFact]
    public Task CreateFolder_Twice_IsIdempotent() => RemoteStoreContractSuite.CreateFolderTwiceIdempotent(Create);

    /// <summary>
    /// MOVE relocates a subtree + Conflict on an occupied target (412).
    /// </summary>
    [IntegrationFact]
    public Task Move_RelocatesSubtree_AndRejectsOccupiedTarget() => RemoteStoreContractSuite.MoveRelocatesSubtree(Create);

    /// <summary>
    /// DELETE removes a subtree, siblings stay intact.
    /// </summary>
    [IntegrationFact]
    public Task Delete_RemovesSubtree_KeepsSiblings() => RemoteStoreContractSuite.DeleteRemovesSubtree(Create);
}
