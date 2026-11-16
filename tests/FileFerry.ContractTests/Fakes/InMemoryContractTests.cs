using FileFerry.Core.Fakes;
using FileFerry.Core.Ports;

namespace FileFerry.ContractTests.Fakes;

/// <summary>
/// <see cref="LocalFileSystemContract"/> run on the in-memory fake — the stand-in for the real one.
/// </summary>
public sealed class InMemoryFileSystemContractTests : LocalFileSystemContract
{
    /// <inheritdoc cref="LocalFileSystemContract.Create"/>
    protected override ILocalFileSystem Create() => new InMemoryFileSystem();
}

/// <summary>
/// <see cref="RemoteStoreContract"/> run on the in-memory "server".
/// </summary>
public sealed class InMemoryRemoteStoreContractTests : RemoteStoreContract
{
    /// <inheritdoc cref="RemoteStoreContract.Create"/>
    protected override IWritableRemoteStore Create() => new InMemoryRemoteStore();
}
