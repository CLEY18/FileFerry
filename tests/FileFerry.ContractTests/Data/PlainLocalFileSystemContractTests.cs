using FileFerry.Core.Ports;
using FileFerry.Data;

namespace FileFerry.ContractTests.Data;

/// <summary>
/// <see cref="LocalFileSystemContract"/> run on the plain-FS adapter: proves on the dev machine
/// that the in-memory fake ≟ a real file system (L substitutability).
/// </summary>
public sealed class PlainLocalFileSystemContractTests : LocalFileSystemContract, IDisposable
{
    private readonly List<string> _createdRoots = [];

    /// <summary>
    /// A fresh temporary root per test; cleaned up in <see cref="Dispose"/>.
    /// </summary>
    protected override ILocalFileSystem Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "fileferry-contract", Guid.NewGuid().ToString("n"));
        _createdRoots.Add(root);
        return new PlainLocalFileSystem(root);
    }

    /// <summary>
    /// Delete all test roots (xunit creates a new class instance for every test).
    /// </summary>
    public void Dispose()
    {
        foreach (var root in _createdRoots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // the OS removes temp directories itself; the test already did its job
            }
        }
    }
}
