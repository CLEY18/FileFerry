using FileFerry.ContractTests;
using FileFerry.Core.Ports;

namespace FileFerry.Client.Tests.WebDav;

/// <summary>
/// Full <see cref="RemoteStoreContract"/> against <see cref="FakeDavServer"/> — i.e. against the
/// real HTTP stack (headers, bodies, status codes), not just memory. This is the "fake-WebDAV"
/// stage of the verification pipeline: it catches adapter protocol-layer defects before a real server.
/// </summary>
[Trait("Category", "Contract")]
public sealed class WebDavHttpFakeContractTests : RemoteStoreContract, IDisposable
{
    private readonly FakeDavServer _server = new();

    /// <inheritdoc cref="RemoteStoreContract.Create"/>
    protected override IWritableRemoteStore Create() => _server.CreateStore();

    /// <summary>
    /// Tear down the HTTP server.
    /// </summary>
    public void Dispose() => _server.Dispose();
}
