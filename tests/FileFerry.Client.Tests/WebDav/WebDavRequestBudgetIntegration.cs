using System.Net.Http;
using FileFerry.Client.WebDav;
using FileFerry.ContractTests;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// The request budget against a live Nextcloud: a planner-shaped write phase (folders first,
/// then files, then a move and a delete) must cost exactly one HTTP request per operation.
/// Ancestors are the plan's own phase — a file write that re-MKCOLs its chain inflates every
/// real upload by the tree depth. Same <c>NC_*</c> gates as the contract integration; without
/// the environment variables every test is honestly SKIPPED.
/// </summary>
[Trait("Category", "Integration")]
public sealed class WebDavRequestBudgetIntegration : IAsyncLifetime
{
    private Uri _baseAddress = new("http://127.0.0.1/");
    private string _user = string.Empty;
    private string _token = string.Empty;

    /// <summary>
    /// Sandbox <c>FileFerryBudget/&lt;guid&gt;/</c> built with a bare client, before any counting.
    /// </summary>
    public async Task InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("NC_URL") ?? "http://127.0.0.1";
        _token = Environment.GetEnvironmentVariable("NC_TOKEN") ?? string.Empty;
        _user = Environment.GetEnvironmentVariable("NC_USER") ?? "guest";

        var root = new Uri(new Uri(url.TrimEnd('/') + "/"), $"remote.php/dav/files/{_user}/");
        var sandbox = new Uri(root, $"FileFerryBudget/{Guid.NewGuid():N}/");
        _baseAddress = sandbox;

        using var http = AuthorizedClient();
        await MkcolAsync(http, new Uri(root, "FileFerryBudget/"));
        await MkcolAsync(http, sandbox);
    }

    /// <summary>
    /// Sandbox teardown: one DELETE takes the subtree; leftovers are disposable by design.
    /// </summary>
    public async Task DisposeAsync()
    {
        using var http = AuthorizedClient();
        try
        {
            using var _ = await http.SendAsync(new HttpRequestMessage(HttpMethod.Delete, _baseAddress));
        }
        catch (HttpRequestException)
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// The write phase of a run — two folders, three files inside them, a move, a delete:
    /// seven operations, seven requests. Per-file ancestor chains would add MKCOLs per file.
    /// </summary>
    [IntegrationFact]
    public async Task PlannerShapedWritePhase_CostsOneRequestPerOperation()
    {
        var counter = new CountingHandler();
        var store = Store(counter); // fresh counter: everything this store does is the counted phase

        (await store.CreateFolderAsync(RemotePath.Parse("d1"), CancellationToken.None)).AssertOk();
        (await store.CreateFolderAsync(RemotePath.Parse("d1/d2"), CancellationToken.None)).AssertOk();
        for (var i = 0; i < 3; i++)
        {
            (await store.WriteAsync(RemotePath.Parse($"d1/d2/f{i}.txt"), new MemoryStream([1, 2, 3]), CancellationToken.None)).AssertOk();
        }

        (await store.MoveAsync(RemotePath.Parse("d1/d2/f0.txt"), RemotePath.Parse("d1/f0.txt"), CancellationToken.None)).AssertOk();
        (await store.DeleteAsync(RemotePath.Parse("d1/d2/f1.txt"), CancellationToken.None)).AssertOk();

        Assert.Equal(7, counter.Total); // every store call is exactly one DAV request
    }

    /// <summary>
    /// A plain listing probe (the move-idempotence shape on the remote side) is one request,
    /// not a walk of the pair.
    /// </summary>
    [IntegrationFact]
    public async Task ExistenceProbe_CostsOneListing()
    {
        var counter = new CountingHandler();
        var store = Store(counter);
        (await store.CreateFolderAsync(RemotePath.Parse("probe"), CancellationToken.None)).AssertOk();
        (await store.WriteAsync(RemotePath.Parse("probe/here.txt"), new MemoryStream([1]), CancellationToken.None)).AssertOk();
        counter.Reset(); // only the probe itself is under budget

        var listing = await store.ListAsync(RemotePath.Parse("probe"), CancellationToken.None);

        listing.AssertOk();
        Assert.Contains(listing.Value, static e => e.Path.Name == "here.txt");
        Assert.Equal(1, counter.Total); // the probe that answers "is it already there?" asks once
    }

    private IWritableRemoteStore Store(CountingHandler counter) =>
        new WebDavRemoteStore(
            new HttpClient(counter) { BaseAddress = _baseAddress, Timeout = TimeSpan.FromMinutes(2) },
            _user,
            _ => Task.FromResult<string?>(_token));

    private HttpClient AuthorizedClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_user}:{_token}")));
        return http;
    }

    private static async Task MkcolAsync(HttpClient http, Uri collection)
    {
        using var response = await http.SendAsync(new HttpRequestMessage(new HttpMethod("MKCOL"), collection));
        if (response.StatusCode != System.Net.HttpStatusCode.Created &&
            response.StatusCode != System.Net.HttpStatusCode.MethodNotAllowed)
        {
            throw new InvalidOperationException($"Cannot prepare the sandbox: MKCOL answered {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    /// Spy over the single transport the store's client owns: counts every request it sends,
    /// <see cref="Reset"/> keeps fixture setup outside the budget.
    /// </summary>
    private sealed class CountingHandler : DelegatingHandler
    {
        private int _count;

        /// <summary>
        /// Requests sent through this spy since the last <see cref="Reset"/>.
        /// </summary>
        public int Total => Volatile.Read(ref _count);

        public CountingHandler()
            : base(new SocketsHttpHandler())
        {
        }

        /// <summary>
        /// Start counting from zero (setup operations stay outside the budget).
        /// </summary>
        public void Reset() => Interlocked.Exchange(ref _count, 0);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _count);
            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }
}
