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
    /// The reviewed truncation: a write body that cannot rewind (the SAF content:// stream) makes
    /// a retry physically empty — after a half-sent attempt the stream is spent, and a second
    /// PUT would stamp an empty file over the target with a "success". The retry must not run
    /// at all; the failure reaches the scheduler, and the next job reopens the source fresh.
    /// </summary>
    [Fact]
    public async Task NonRewindableWrite_FailsOnce_NeverRetries()
    {
        var inner = new RecordingWriteStore(failures: 1, SyncError.Of(ErrorKind.Network, "half-sent, wifi died"));
        var delay = new RecordingDelay();
        var store = new RetryingRemoteStore(inner, Options, delay, new Random(7));

        var result = await store.WriteAsync(
            RemotePath.Parse("doc.bin"), new ForwardOnlyStream("payload"u8.ToArray()), CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorKind.Network, result.Error.Kind);
        Assert.Equal(1, inner.WriteCalls); // no second attempt: an empty retry would overwrite the target
        Assert.Empty(delay.Delays);
    }

    /// <summary>
    /// A rewindable body retries — and resends from the point the caller handed the stream at,
    /// not from an arbitrary zero (a body read from mid-stream must not gain the skipped head).
    /// </summary>
    [Fact]
    public async Task RewindableWrite_RetryResendsFromTheStartingPoint()
    {
        var inner = new RecordingWriteStore(failures: 1, SyncError.Of(ErrorKind.Network, "connection reset"));
        var delay = new RecordingDelay();
        var store = new RetryingRemoteStore(inner, Options, delay, new Random(7));
        using var body = new MemoryStream("skip-me-tail"u8.ToArray());
        body.Position = 8; // the caller's stream starts mid-content

        var result = await store.WriteAsync(RemotePath.Parse("doc.bin"), body, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, inner.WriteBodies.Count);
        Assert.Equal("tail"u8.ToArray(), inner.WriteBodies[0]); // first attempt: from the starting point
        Assert.Equal("tail"u8.ToArray(), inner.WriteBodies[1]); // retry: the same range again, not zero, not empty
    }

    /// <summary>
    /// Store that fails the first N writes with the given error and records the exact bytes
    /// each attempt read out of the body.
    /// </summary>
    private sealed class RecordingWriteStore(int failures, SyncError error) : IWritableRemoteStore
    {
        private readonly InMemoryRemoteStore _inner = new();
        private int _writeCalls;

        /// <summary>
        /// Number of WriteAsync calls (attempt-count assertions).
        /// </summary>
        public int WriteCalls => _writeCalls;

        /// <summary>
        /// Body bytes each attempt managed to read, in attempt order.
        /// </summary>
        public List<byte[]> WriteBodies { get; } = [];

        public Task<Result<IReadOnlyList<RemoteEntry>, SyncError>> ListAsync(RemotePath folder, CancellationToken ct) => _inner.ListAsync(folder, ct);

        public Task<Result<Stream, SyncError>> ReadAsync(RemotePath file, CancellationToken ct) => _inner.ReadAsync(file, ct);

        public async Task<Result<RemoteEntry, SyncError>> WriteAsync(RemotePath file, Stream content, CancellationToken ct)
        {
            var seen = Interlocked.Increment(ref _writeCalls);
            using var drained = new MemoryStream();
            await content.CopyToAsync(drained, ct).ConfigureAwait(false);
            WriteBodies.Add(drained.ToArray());
            return seen <= failures
                ? Result<RemoteEntry, SyncError>.Failure(error)
                : await _inner.WriteAsync(file, content, ct).ConfigureAwait(false);
        }

        public Task<Result<RemoteEntry, SyncError>> CreateFolderAsync(RemotePath folder, CancellationToken ct) => _inner.CreateFolderAsync(folder, ct);

        public Task<Result<Ok, SyncError>> MoveAsync(RemotePath from, RemotePath to, CancellationToken ct) => _inner.MoveAsync(from, to, ct);

        public Task<Result<Ok, SyncError>> DeleteAsync(RemotePath path, CancellationToken ct) => _inner.DeleteAsync(path, ct);
    }

    /// <summary>
    /// Forward-only body (CanSeek false) — the shape of a SAF content:// input stream.
    /// </summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        /// <summary>
        /// Wrapper over <paramref name="payload"/> hiding every seek affordance.
        /// </summary>
        internal ForwardOnlyStream(byte[] payload) => _inner = new MemoryStream(payload);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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

