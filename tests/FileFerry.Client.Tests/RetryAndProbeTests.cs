using System.Net;
using System.Text;
using FileFerry.Client;
using FileFerry.Client.Retry;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests;

/// <summary>
/// Timing of <see cref="RetryingRemoteStore"/>: transient failures only, full-jitter bounds,
/// delay budgets.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RetryingRemoteStoreTests
{
    private static readonly RetryPolicyOptions Options = new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(100),
        MaxDelay = TimeSpan.FromMilliseconds(800),
        MaxAttempts = 4,
        MaxTotalDelay = TimeSpan.FromMilliseconds(5000),
    };

    [Fact]
    public async Task TransientFailure_Retries_ThenSucceeds()
    {
        var inner = new FlakyStore(new InMemoryRemoteStore(), 2, SyncError.Of(ErrorKind.Network, "cable unplugged"));
        var delay = new RecordingDelay();
        var store = new RetryingRemoteStore(inner, Options, delay, new Random(7));

        var result = await store.CreateFolderAsync(RemotePath.Parse("sync"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, inner.CreateFolderCalls); // 1 initial + 2 retries
        Assert.Equal(2, delay.Delays.Count);
        Assert.All(delay.Delays, static d => Assert.True(d >= TimeSpan.Zero && d <= TimeSpan.FromMilliseconds(800)));
    }

    [Fact]
    public async Task PermanentFailure_ReturnsImmediately_NoSleep()
    {
        var inner = new FlakyStore(new InMemoryRemoteStore(), int.MaxValue, SyncError.Of(ErrorKind.AuthExpired, "token revoked"));
        var delay = new RecordingDelay();
        var store = new RetryingRemoteStore(inner, Options, delay, new Random(7));

        var result = await store.CreateFolderAsync(RemotePath.Parse("sync"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.AuthExpired, result.Error.Kind);
        Assert.Equal(1, inner.CreateFolderCalls);
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task ExhaustedBudget_GivesUpWithinLimits()
    {
        var inner = new FlakyStore(new InMemoryRemoteStore(), int.MaxValue, SyncError.Of(ErrorKind.ServerGone, "server is silent"));
        var delay = new RecordingDelay();
        var store = new RetryingRemoteStore(inner, Options, delay, new Random(7));

        var result = await store.CreateFolderAsync(RemotePath.Parse("sync"), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(Options.MaxAttempts + 1, inner.CreateFolderCalls); // exactly as many attempts as the policy promises
        Assert.True(TimeSpan.FromTicks(delay.Delays.Sum(static d => d.Ticks)) <= Options.MaxTotalDelay);
    }

    [Theory]
    [InlineData(0, 0.5, 50)]   // unit·min(cap, base·2^0) = 0.5·100
    [InlineData(3, 1.0, 800)]  // MaxDelay cap wins on later attempts
    public void NextDelay_FullJitter_StaysInPolicyBounds(int attempt, double unit, double expectedMs) =>
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), RetryingRemoteStore.NextDelay(Options, attempt, unit));

    /// <summary>
    /// Store whose first N CreateFolder calls fail with the given (transient or arbitrary)
    /// error; calls after that succeed.
    /// </summary>
    private sealed class FlakyStore(InMemoryRemoteStore inner, int failures, SyncError error) : IWritableRemoteStore
    {
        private int _createFolderCalls;

        /// <summary>
        /// How many times CreateFolder was called (attempt-count assertions).
        /// </summary>
        public int CreateFolderCalls => _createFolderCalls;

        public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct) => inner.ListAsync(folder, ct);

        public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) => inner.ReadAsync(file, ct);

        public Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct) => inner.WriteAsync(file, content, ct);

        public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct)
        {
            return ++_createFolderCalls <= failures
                ? Task.FromResult(Result<RemoteEntry, SyncError>.Failure(error))
                : inner.CreateFolderAsync(folder, ct);
        }

        public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct) => inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct) => inner.DeleteAsync(path, ct);
    }

    /// <summary>
    /// A "sleep" that records requested delays instead of waiting — deterministic timing tests.
    /// </summary>
    private sealed class RecordingDelay : ITaskDelay
    {
        /// <summary>
        /// Recorded delays in request order.
        /// </summary>
        public List<TimeSpan> Delays { get; } = [];

        /// <inheritdoc cref="ITaskDelay.DelayAsync"/>
        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// status.php probe: success, "server gone" (network/timeout), unparseable body.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WebDavAvailabilityProbeTests
{
    [Fact]
    public async Task Probe_ParsesRealStatusShape()
    {
        // Recorded from a real Nextcloud 34.0.3: fields are TOP-LEVEL,
        // status.php has no "data" node (that shape belongs to the OCS API).
        var http = Probe("200", """
            { "installed": true, "maintenance": false, "needsDbUpgrade": false,
              "version": "34.0.3.2", "versionstring": "34.0.3", "edition": "",
              "productname": "Nextcloud", "extendedSupport": false }
            """);

        var status = await new WebDavAvailabilityProbe(http).ProbeAsync(CancellationToken.None);

        Assert.True(status.IsSuccess);
        Assert.Equal("34.0.3.2", status.Value.Version);
        Assert.False(status.Value.MaintenanceMode);
    }

    [Fact]
    public async Task Probe_Maintenance_SurfacesFlag()
    {
        var http = Probe("200", """{ "installed": true, "maintenance": true, "version": "31.0.0.1" }""");

        var status = await new WebDavAvailabilityProbe(http).ProbeAsync(CancellationToken.None);

        Assert.True(status.Value.MaintenanceMode);
    }

    [Fact]
    public async Task Probe_ConnectionRefused_IsServerGone()
    {
        var http = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")))
        {
            BaseAddress = new Uri("http://cloud.example/"),
        };

        var status = await new WebDavAvailabilityProbe(http).ProbeAsync(CancellationToken.None);

        Assert.Equal(ErrorKind.ServerGone, status.Error.Kind);
    }

    [Fact]
    public async Task Probe_GarbageBody_IsUnknown()
    {
        var http = Probe("500", "<html>apache is down</html>");

        var status = await new WebDavAvailabilityProbe(http).ProbeAsync(CancellationToken.None);

        Assert.Equal(ErrorKind.Unknown, status.Error.Kind);
    }

    /// <summary>
    /// HttpClient with a stubbed response.
    /// </summary>
    private static HttpClient Probe(string status, string body) =>
        new(new StubHandler(new HttpResponseMessage(Enum.Parse<HttpStatusCode>(status))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }))
        {
            BaseAddress = new Uri("http://cloud.example/"),
        };

    /// <summary>
    /// HTTP handler with a fixed response.
    /// </summary>
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        /// <inheritdoc cref="HttpMessageHandler.SendAsync"/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(response);
    }

    /// <summary>
    /// HTTP handler that throws a network exception.
    /// </summary>
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        /// <inheritdoc cref="HttpMessageHandler.SendAsync"/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw exception;
    }
}

