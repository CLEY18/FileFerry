using FileFerry.Core.Domain.Paths;

namespace FileFerry.ContractTests;

/// <summary>
/// Smoke placeholder: confirms the contract-test project is wired to Core and Fakes and runs
/// through the `dotnet test` pipeline.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WiringTests
{
    [Fact]
    public void ContractHarness_SeesCoreAndFakesAssemblies()
    {
        // Both sides of the contract must resolve within a single test run.
        Assert.NotNull(typeof(RemotePath).Assembly);
        Assert.Equal(
            typeof(RemotePath).Assembly.GetName().Name,
            typeof(FileFerry.Core.Domain.Paths.RelativePath).Assembly.GetName().Name);
    }
}
