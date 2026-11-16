using FileFerry.Client;
using FileFerry.Core.Domain.Paths;

namespace FileFerry.Client.Tests;

/// <summary>
/// Smoke placeholder: Client.Tests sees Core (port-side types) and the Client assembly.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WiringTests
{
    [Fact]
    public void ClientAndCoreAreReferenced()
    {
        Assert.NotNull(typeof(RemotePath).Assembly);
        Assert.Equal("FileFerry.Client", AssemblyMarker.Name);
    }
}

