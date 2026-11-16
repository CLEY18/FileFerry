using System.Net;
using System.Text;
using FileFerry.Client.LoginFlow;
using FileFerry.Client.Retry;
using FileFerry.ContractTests;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests.LoginFlow;

/// <summary>
/// Unit tests for <see cref="LoginFlowV2Client"/> on crafted Nextcloud "Login flow v2" responses:
/// HTTP is stubbed with a scripted <see cref="HttpMessageHandler"/>, pauses with a deterministic fake.
/// Also pins that secrets never leak into <c>ToString()</c> or error output.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LoginFlowV2ClientTests
{
    private const string PollToken = "Ad0MS3JPFP2VdNxQVt6PJX7UvzFtHc3W3dS7xS-nkA9Lq4lP";
    private const string AppPassword = "abcde-fghij-klmno-pqrst-uvwxy";

    /// <summary>
    /// Init response as POST index.php/login/v2 answers it: a server object, a consent URL and a poll endpoint.
    /// </summary>
    private const string InitFixture = """
        {
          "server": { "name": "Home", "host": "cloud.example", "version": "16.0.0.7",
                      "filters": { "mode": "whitelist", "unreachable": false } },
          "login": "https://cloud.example/login/v2/flow?token=4TsAHHGhvH5RChDyV9zBbD6WdkEqeP-p",
          "poll": { "token": "Ad0MS3JPFP2VdNxQVt6PJX7UvzFtHc3W3dS7xS-nkA9Lq4lP",
                    "endpoint": "https://cloud.example/index.php/login/v2/poll" }
        }
        """;

    /// <summary>
    /// Poll response after consent (200): flow v2 answers "server" as a URL string, not an object.
    /// </summary>
    private const string PollApprovedFixture = """
        {
          "server": "https://cloud.example",
          "loginName": "alice",
          "appPassword": "abcde-fghij-klmno-pqrst-uvwxy"
        }
        """;

    private static readonly Uri ServerBase = new("https://cloud.example/");

    [Fact]
    public async Task Start_ParsesInitFixture()
    {
        var handler = new ScriptedHandler(
            Json(HttpStatusCode.OK, InitFixture));
        var flow = new LoginFlowV2Client(new HttpClient(handler));

        var challenge = await flow.StartAsync(ServerBase, CT);

        challenge.AssertOk();
        Assert.Equal("https://cloud.example/login/v2/flow?token=4TsAHHGhvH5RChDyV9zBbD6WdkEqeP-p",
            challenge.Value.LoginUrl.ToString());
        Assert.Equal("https://cloud.example/index.php/login/v2/poll", challenge.Value.PollEndpoint.ToString());
        Assert.Equal(PollToken, challenge.Value.PollToken);
        Assert.Equal("Home", challenge.Value.ServerName);
        Assert.Equal("16.0.0.7", challenge.Value.ServerVersion);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://cloud.example/index.php/login/v2", request.RequestUri!.ToString());
        Assert.Equal("FileFerry", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task Start_SubdirectoryServer_AppendsIndexPathUnderPrefix()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.OK, InitFixture));
        var flow = new LoginFlowV2Client(new HttpClient(handler));

        await flow.StartAsync(new Uri("https://host.example/cloud"), CT);

        Assert.Equal("https://host.example/cloud/index.php/login/v2", Assert.Single(handler.Requests).RequestUri!.ToString());
    }

    [Fact]
    public async Task Start_UnknownRoute_ReportsNotFoundWithoutBody()
    {
        var handler = new ScriptedHandler(Text(HttpStatusCode.NotFound, "<html>404</html>"));
        var flow = new LoginFlowV2Client(new HttpClient(handler));

        var error = (await flow.StartAsync(ServerBase, CT)).Error;

        Assert.Equal(ErrorKind.NotFound, error.Kind);
        Assert.Contains("login/v2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_NetworkDown_ReportsNetwork()
    {
        var flow = new LoginFlowV2Client(new HttpClient(new ScriptedHandler(
            new HttpRequestException("no route to host"))));

        var error = (await flow.StartAsync(ServerBase, CT)).Error;

        Assert.Equal(ErrorKind.Network, error.Kind);
    }

    [Fact]
    public async Task Poll_WhileUserDecides_ReturnsPending()
    {
        // the poll endpoint answers 404 until the user has consented — that is Pending, not an error
        var flow = new LoginFlowV2Client(new HttpClient(new ScriptedHandler(
            Text(HttpStatusCode.NotFound, string.Empty))));

        var outcome = await flow.PollAsync(Challenge(), CT);

        outcome.AssertOk();
        Assert.Equal(LoginPollStatus.Pending, outcome.Value.Status);
    }

    [Fact]
    public async Task Poll_Legacy400_StillPending()
    {
        var flow = new LoginFlowV2Client(new HttpClient(new ScriptedHandler(
            Text(HttpStatusCode.BadRequest, string.Empty))));

        var outcome = await flow.PollAsync(Challenge(), CT);

        outcome.AssertOk();
        Assert.Equal(LoginPollStatus.Pending, outcome.Value.Status);
    }

    [Fact]
    public async Task Poll_ApprovedFixture_ReturnsCredentials_AndPollPostsToken()
    {
        var handler = new ScriptedHandler(Json(HttpStatusCode.OK, PollApprovedFixture));
        var flow = new LoginFlowV2Client(new HttpClient(handler));

        var outcome = await flow.PollAsync(Challenge(), CT);

        outcome.AssertOk();
        var credentials = outcome.Value.Credentials!;
        Assert.Equal("alice", credentials.LoginName);
        Assert.Equal(AppPassword, credentials.AppPassword);
        Assert.Equal("https://cloud.example/", credentials.ServerBaseUrl.ToString());
        Assert.Equal("16.0.0.7", credentials.ServerVersion);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://cloud.example/index.php/login/v2/poll", request.RequestUri!.ToString());
        Assert.Equal($"token={Uri.EscapeDataString(PollToken)}", handler.Bodies[0]);
    }

    [Fact]
    public async Task Poll_ApprovedServerAsUrlString_DerivesServerNameFromHost()
    {
        // flow v2 answers "server" as a URL string rather than an object; parsing must not lose credentials.
        var flow = new LoginFlowV2Client(new HttpClient(new ScriptedHandler(
            Json(HttpStatusCode.OK, """
                {
                  "server": "https://nc.example",
                  "loginName": "alice",
                  "appPassword": "abcde-fghij-klmno-pqrst-uvwxy"
                }
                """))));

        var outcome = await flow.PollAsync(Challenge() with { ServerName = string.Empty }, CT);

        outcome.AssertOk();
        var credentials = outcome.Value.Credentials!;
        Assert.Equal("alice", credentials.LoginName);
        Assert.Equal("nc.example", credentials.ServerName);
    }

    [Fact]
    public async Task Poll_ApprovedLegacyServerObject_KeepsNameAndVersion()
    {
        var flow = new LoginFlowV2Client(new HttpClient(new ScriptedHandler(
            Json(HttpStatusCode.OK, """
                {
                  "server": { "name": "Home", "version": "30.0.2" },
                  "loginName": "alice",
                  "appPassword": "abcde-fghij-klmno-pqrst-uvwxy"
                }
                """))));

        var outcome = await flow.PollAsync(Challenge() with { ServerName = string.Empty, ServerVersion = string.Empty }, CT);

        outcome.AssertOk();
        var credentials = outcome.Value.Credentials!;
        Assert.Equal("Home", credentials.ServerName);
        Assert.Equal("30.0.2", credentials.ServerVersion);
    }

    [Fact]
    public async Task Poll_GarbageBody_FailsUnknownWithoutLeakingToken()
    {
        var flow = new LoginFlowV2Client(new HttpClient(new ScriptedHandler(
            Text(HttpStatusCode.OK, "<!doctype html>proxy error"))));

        var error = (await flow.PollAsync(Challenge(), CT)).Error;

        Assert.Equal(ErrorKind.Unknown, error.Kind);
        Assert.DoesNotContain(PollToken, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_PollsWhilePending_ThenReturnsCredentials()
    {
        var handler = new ScriptedHandler(
            Text(HttpStatusCode.NotFound, string.Empty),
            Text(HttpStatusCode.NotFound, string.Empty),
            Json(HttpStatusCode.OK, PollApprovedFixture));
        var delay = new RecordingDelay();
        var flow = new LoginFlowV2Client(new HttpClient(handler), LoginFlowOptions.Defaults, delay);

        var credentials = await flow.CompleteAsync(Challenge(), CT);

        credentials.AssertOk();
        Assert.Equal("alice", credentials.Value.LoginName);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, delay.Delays.Count); // pause only between attempts, not after the last one
        Assert.All(delay.Delays, d => Assert.Equal(LoginFlowOptions.Defaults.PollInterval, d));
    }

    [Fact]
    public async Task Complete_RateLimited_BacksOffAndKeepsWaiting()
    {
        var handler = new ScriptedHandler(
            Text((HttpStatusCode)429, string.Empty),
            Json(HttpStatusCode.OK, PollApprovedFixture));
        var delay = new RecordingDelay();
        var flow = new LoginFlowV2Client(new HttpClient(handler), LoginFlowOptions.Defaults, delay);

        var credentials = await flow.CompleteAsync(Challenge(), CT);

        credentials.AssertOk();
        var expected = LoginFlowOptions.Defaults.PollInterval * LoginFlowOptions.Defaults.RateLimitBackoffFactor;
        Assert.Equal(expected, Assert.Single(delay.Delays));
    }

    [Fact]
    public async Task Complete_PermanentFailure_StopsImmediately()
    {
        var handler = new ScriptedHandler(Text(HttpStatusCode.Forbidden, "IP banned"));
        var delay = new RecordingDelay();
        var flow = new LoginFlowV2Client(new HttpClient(handler), LoginFlowOptions.Defaults, delay);

        var error = (await flow.CompleteAsync(Challenge(), CT)).Error;

        Assert.Equal(ErrorKind.PermissionLost, error.Kind);
        Assert.Single(handler.Requests); // no retries on a permanent failure
        Assert.Empty(delay.Delays);
    }

    [Fact]
    public async Task Complete_NeverApproved_GivesUpAfterMaxWait()
    {
        var options = LoginFlowOptions.Defaults with
        {
            PollInterval = TimeSpan.FromSeconds(1),
            MaxWait = TimeSpan.FromSeconds(2),
        };
        var handler = new ScriptedHandler(Text(HttpStatusCode.NotFound, string.Empty));
        var delay = new RecordingDelay();
        var flow = new LoginFlowV2Client(new HttpClient(handler), options, delay);

        var error = (await flow.CompleteAsync(Challenge(), CT)).Error;

        Assert.Equal(ErrorKind.Network, error.Kind);
        Assert.Equal(2, delay.Delays.Count); // pause budget equals MaxWait — no more
        Assert.Equal(TimeSpan.FromSeconds(2), TimeSpan.FromTicks(delay.Delays.Sum(static d => d.Ticks)));
    }

    [Fact]
    public void Secrets_AreMaskedInDiagnostics()
    {
        var challenge = Challenge();
        Assert.DoesNotContain(PollToken, challenge.ToString(), StringComparison.Ordinal);

        var credentials = LoginPollOutcome.Authorized(new LoginCredentials(
            ServerBase, "alice", AppPassword, "Home", "16.0.0.7")).Credentials!;
        Assert.DoesNotContain(AppPassword, credentials.ToString(), StringComparison.Ordinal);
    }

    private static readonly CancellationToken CT = CancellationToken.None;

    /// <summary>
    /// Challenge stitched from the same fixture pair.
    /// </summary>
    private static LoginChallenge Challenge() => new(
        new Uri("https://cloud.example/login/v2/flow?token=4TsAHHGhvH5RChDyV9zBbD6WdkEqeP-p"),
        new Uri("https://cloud.example/index.php/login/v2/poll"),
        PollToken,
        "Home",
        "16.0.0.7");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => Text(status, body, "application/json");

    private static HttpResponseMessage Text(HttpStatusCode status, string body, string mediaType = "text/plain")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    /// <summary>
    /// Scripted "server": replays pre-recorded responses; a thrown exception is a network failure.
    /// </summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<Task<HttpResponseMessage>>[] _script;
        private int _index;

        /// <summary>
        /// All requests that reached the "server" (for checking URLs/methods).
        /// </summary>
        public List<HttpRequestMessage> Requests { get; } = [];

        /// <summary>
        /// Request bodies in arrival order (for checking the poll form shape).
        /// </summary>
        public List<string> Bodies { get; } = [];

        /// <summary>
        /// Script of ready responses; a thrown exception emulates a network failure.
        /// </summary>
        internal ScriptedHandler(params Func<Task<HttpResponseMessage>>[] script) => _script = script;

        /// <summary>
        /// Script of concrete responses (wrapper over the function-based ctor).
        /// </summary>
        internal ScriptedHandler(params HttpResponseMessage[] responses)
            : this([.. responses.Select<HttpResponseMessage, Func<Task<HttpResponseMessage>>>(r => () => Task.FromResult(Clone(r)))])
        {
        }

        /// <summary>
        /// Script with a single "network failure" — the thrown exception emulates a lost connection.
        /// </summary>
        internal ScriptedHandler(Exception networkFailure)
            : this([() => throw networkFailure])
        {
        }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct));
            var respond = _script[Math.Min(_index, _script.Length - 1)]; // the tail repeats — "always 400" etc.
            _index++;
            return await respond();
        }

        private static HttpResponseMessage Clone(HttpResponseMessage response)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var clone = new HttpResponseMessage(response.StatusCode) { Content = new StringContent(body) };
            foreach (var header in response.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return clone;
        }
    }

    /// <summary>
    /// Deterministic pause mechanics: records the requested duration instead of sleeping.
    /// </summary>
    private sealed class RecordingDelay : ITaskDelay
    {
        /// <summary>
        /// Recorded pauses (in policy units).
        /// </summary>
        public List<TimeSpan> Delays { get; } = [];

        /// <inheritdoc />
        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }
}
