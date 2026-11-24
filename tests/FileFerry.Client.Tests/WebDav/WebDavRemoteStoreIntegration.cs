using FileFerry.Client.WebDav;
using FileFerry.ContractTests;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// Integration of <see cref="WebDavRemoteStore"/> against a live Nextcloud:
/// <c>NC_URL=… NC_USER=… NC_TOKEN=… dotnet test --filter Category=Integration</c>.
/// Each test runs in its own sandbox <c>FileFerryContract/&lt;guid&gt;/</c> — created before the
/// first request and deleted afterwards: WebDAV rejects writes into a missing parent (409),
/// so the sandbox cannot be assumed to exist. Without the environment variables every
/// <see cref="IntegrationFactAttribute"/> test is honestly SKIPPED.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WebDavRemoteStoreIntegration : IAsyncLifetime
{
    private Uri _baseAddress = new("http://127.0.0.1/");
    private Uri _sandbox = new("http://127.0.0.1/");
    private string _user = string.Empty;
    private string _token = string.Empty;

    /// <summary>
    /// Live-server configuration from the environment (the attributes guarantee the gates are
    /// set) and the sandbox itself, built with a bare HTTP client — the fixture must not
    /// depend on the store under test.
    /// </summary>
    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("NC_URL") ?? "http://127.0.0.1";
        _token = Environment.GetEnvironmentVariable("NC_TOKEN") ?? string.Empty;
        _user = Environment.GetEnvironmentVariable("NC_USER") ?? "guest";

        // Unique sandbox per test instance: parallel Facts don't interfere (the token lives only here).
        var root = new Uri(new Uri(url.TrimEnd('/') + "/"), $"remote.php/dav/files/{_user}/");
        _sandbox = new Uri(root, $"FileFerryContract/{Guid.NewGuid():N}/");
        _baseAddress = _sandbox;

        using var http = Probe();
        await CreateCollectionAsync(http, new Uri(root, "FileFerryContract/"), CancellationToken.None);
        await CreateCollectionAsync(http, _sandbox, CancellationToken.None);
    }

    /// <summary>
    /// Sandbox teardown: one DELETE takes the whole subtree. Failures stay quiet — a leftover
    /// <c>FileFerryContract/&lt;guid&gt;/</c> is disposable by design, not a test result.
    /// </summary>
    public async Task DisposeAsync()
    {
        using var http = Probe();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, _sandbox);
            await http.SendAsync(request, CancellationToken.None);
        }
        catch (HttpRequestException)
        {
            // best-effort cleanup
        }
    }

    private HttpClient Probe()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_user}:{_token}")));
        return http;
    }

    private static async Task CreateCollectionAsync(HttpClient http, Uri collection, CancellationToken ct)
    {
        using var response = await http.SendAsync(
            new HttpRequestMessage(new HttpMethod("MKCOL"), collection), ct);
        // 201 = created; 405 = the container already existed — both usable states.
        if (response.StatusCode != System.Net.HttpStatusCode.Created &&
            response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
        {
            throw new InvalidOperationException(
                $"Cannot prepare the sandbox {collection.AbsolutePath}: MKCOL answered {(int)response.StatusCode}.");
        }
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
    /// A write into a missing collection is refused without creating a file (the live server answers 404).
    /// </summary>
    [IntegrationFact]
    public Task Write_MissingParent_Refused() => RemoteStoreContractSuite.WriteMissingParentRefused(Create);

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
