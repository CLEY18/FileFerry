using FileFerry.Core.Domain;

namespace FileFerry.ContractTests;

/// <summary>
/// Assertions for <see cref="Result{TValue,TError}"/>: the failure message is computed lazily
/// (the usual <c>Assert.True(ok, error.ToString())</c> breaks on success — Error throws).
/// </summary>
public static class ResultAsserts
{
    /// <summary>
    /// Asserts success; otherwise — fail with the failure text.
    /// </summary>
    public static void AssertOk<TValue>(this Result<TValue, SyncError> result)
    {
        if (result.IsFailure)
        {
            Assert.Fail($"Expected success, got failure: {result.Error}");
        }
    }

    /// <summary>
    /// Asserts a failure of the given kind; otherwise — fail with context.
    /// </summary>
    public static void AssertFail<TValue>(this Result<TValue, SyncError> result, ErrorKind expected)
    {
        if (result.IsSuccess)
        {
            Assert.Fail($"Expected failure {expected}, got success.");
        }

        Assert.Equal(expected, result.Error.Kind);
    }
}
