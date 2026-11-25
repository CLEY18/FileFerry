using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using FileFerry.Client.WebDav;
using FileFerry.Core.Domain;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;
using FileFerry.Core.Sync.Execution;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// Server answers the transfer must survive without buffering or crashing: a metadata body over
/// its cap is refused (an inflated gzip body counts decoded), a file body streams — the open
/// returns while the body still flows — and a body cut mid-transfer fails the step, not the app.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BoundedResponseTests : IDisposable
{
    private readonly FakeRawServer _server = new();

    /// <summary>
    /// A valid-looking PROPFIND body padded past every honest directory, then cut off.
    /// </summary>
    [Fact]
    public async Task List_OversizedBody_RefusedWithACapError()
    {
        _server.RespondWith(async ctx =>
        {
            ctx.Response.StatusCode = 207;
            await using var body = ctx.Response.OutputStream;
            var filler = Encoding.UTF8.GetBytes($"<response>{new string('x', 80_000)}</response>");
            await body.WriteAsync(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">"));
            for (var i = 0; i < 120; i++)
            {
                await body.WriteAsync(filler); // ≈ 9.6 MiB — over any honest cap
            }
        });

        var list = await _server.CreateStore().ListAsync(RemotePath.Parse("sync"), CancellationToken.None);

        Assert.True(list.IsFailure);
        Assert.True(
            list.Error.Message.Contains("cap", StringComparison.OrdinalIgnoreCase),
            $"the refusal should name the cap: {list.Error.Message}");
    }

    ///
    /// A small gzip body that inflates past the cap must fail the same way — the cap counts
    /// decoded bytes, not what the wire carried.
    ///
    [Fact]
    public async Task List_GzipBodyExpandingPastTheCap_Refused()
    {
        _server.RespondWith(async ctx =>
        {
            ctx.Response.StatusCode = 207;
            ctx.Response.Headers["Content-Encoding"] = "gzip";
            await using var gzip = new GZipStream(ctx.Response.OutputStream, CompressionMode.Compress, leaveOpen: true);
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\">"));
            var filler = new byte[1024 * 1024]; // compresses away — the decoded count is what matters
            for (var i = 0; i < 12; i++)
            {
                await gzip.WriteAsync(filler);
            }
        });

        var list = await _server.CreateStore().ListAsync(RemotePath.Parse("sync"), CancellationToken.None);

        Assert.True(list.IsFailure);
        Assert.True(
            list.Error.Message.Contains("cap", StringComparison.OrdinalIgnoreCase),
            $"the refusal should name the cap: {list.Error.Message}");
    }

    /// <summary>
    /// The port contract is a stream: the open completes on the headers, while the server is
    /// still mid-body — a full-body copy before returning would buffer multi-gigabyte files.
    /// </summary>
    [Fact]
    public async Task Read_BigBodyStillFlowing_StreamReturnsAtOnce()
    {
        _server.RespondWith(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await using var body = ctx.Response.OutputStream;
            await body.WriteAsync(new byte[1024 * 1024]);
            await body.FlushAsync(); // the first megabyte is on the wire…
            await Task.Delay(6000);   // …the rest dawdles
            try
            {
                await body.WriteAsync(new byte[1024]);
            }
            catch (Exception)
            {
                // the test may finish (and hang up) before the dawdling server catches up
            }
        });

        var stopwatch = Stopwatch.StartNew();
        var read = await _server.CreateStore().ReadAsync(RemotePath.Parse("big.bin"), CancellationToken.None);
        stopwatch.Stop();

        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : string.Empty);
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"the open waited {stopwatch.ElapsedMilliseconds} ms — the adapter buffers the body");
        var first = new byte[64];
        Assert.True(await read.Value.ReadAsync(first) > 0);
        await read.Value.DisposeAsync();
    }

    /// <summary>
    /// Streaming must lose nothing: a complete 5 MiB body arrives byte-exact through the port.
    /// </summary>
    [Fact]
    public async Task Read_FullBody_YieldsEveryByte()
    {
        const int Size = 5 * 1024 * 1024;
        _server.RespondWith(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            await using var body = ctx.Response.OutputStream;
            var block = new byte[64 * 1024];
            for (var i = 0; i < Size / block.Length; i++)
            {
                block.AsSpan().Fill((byte)(i % 251));
                await body.WriteAsync(block);
            }
        });

        var read = await _server.CreateStore().ReadAsync(RemotePath.Parse("whole.bin"), CancellationToken.None);
        Assert.True(read.IsSuccess, read.IsFailure ? read.Error.Message : string.Empty);
        await using (read.Value)
        {
            using var received = new MemoryStream();
            await read.Value.CopyToAsync(received);
            Assert.Equal(Size, received.Length);
        }
    }

    /// <summary>
    /// A body promised at 100 000 bytes and cut at 50 000: the download fails as an error with
    /// nothing half-written on the device — never as a crash, never as a truncated file.
    /// </summary>
    [Fact]
    public async Task Download_BodyCutMidTransfer_FailsTheStep_LeavesNoPartialFile()
    {
        _server.RespondWith(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentLength64 = 100_000;
            await ctx.Response.OutputStream.WriteAsync(new byte[50_000]);
            await ctx.Response.OutputStream.FlushAsync();
            ctx.Response.Abort(); // the connection dies mid-body
        });

        var local = new InMemoryFileSystem(new MutableClock());
        var executor = new OperationExecutor(
            local, _server.CreateStore(), new InMemorySyncStateRepository(),
            new StashPolicy(new StashPolicyOptions()), OperationExecutor.DefaultHandlers());
        var pair = new FolderPair(Guid.NewGuid(), Guid.NewGuid(), "sync", "tree", "Sync");
        var context = new SyncRunContext(
            pair, DateTimeOffset.UnixEpoch,
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>(),
            new Dictionary<RelativePath, FileSnapshot>());
        var operation = new SyncOperation(
            OperationKind.DownloadToLocal,
            RelativePath.Parse("big.bin"),
            null,
            new FileSnapshot(RelativePath.Parse("big.bin"), EntryKind.File, 100_000));

        var report = await executor.ExecuteAsync(context, [operation], CancellationToken.None);

        Assert.False(report.Completed);
        Assert.NotNull(report.Error);
        var files = await local.SnapshotAsync(CancellationToken.None);
        Assert.DoesNotContain(files.Value, static s => s.Path.ToString() == "big.bin");
    }

    public void Dispose() => _server.Dispose();

    /// <summary>
    /// Minimal scripted HTTP server: every request runs through the test's current responder.
    /// </summary>
    private sealed class FakeRawServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private volatile Func<HttpListenerContext, Task>? _responder;

        public Uri BaseAddress { get; }

        public FakeRawServer()
        {
            var port = GetFreePort();
            BaseAddress = new Uri($"http://127.0.0.1:{port}/dav/files/anna/");
            _listener.Prefixes.Add(BaseAddress.ToString());
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public void RespondWith(Func<HttpListenerContext, Task> handler) => _responder = handler;

        public WebDavRemoteStore CreateStore() =>
            new(DavHttpClient.Create(BaseAddress), "anna", static _ => Task.FromResult<string?>("secret-app-token"));

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // stopped
                }

                var responder = _responder;
                if (responder is null)
                {
                    context.Response.StatusCode = 500;
                    context.Response.Close();
                    continue;
                }

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await responder(context);
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                        try
                        {
                            context.Response.Abort();
                        }
                        catch (Exception)
                        {
                            // the client is already gone
                        }
                    }
                });
            }
        }

        private static int GetFreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            try
            {
                _listener.Stop();
                _listener.Close();
            }
            catch (Exception)
            {
                // already down
            }
        }
    }
}
