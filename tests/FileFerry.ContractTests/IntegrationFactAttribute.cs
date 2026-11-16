using Xunit;

namespace FileFerry.ContractTests;

/// <summary>
/// A <see cref="FactAttribute"/> that becomes statically SKIPPED while <c>NC_URL</c> + <c>NC_TOKEN</c>
/// are unset. Integration tests stay visible in discovery without painting the default run red.
/// </summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    /// <summary>
    /// The environment-variable gates are read at the attribute's discovery stage.
    /// </summary>
    public IntegrationFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NC_URL"))
            || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NC_TOKEN")))
        {
            Skip = "NC_URL/NC_TOKEN not set — the integration run needs a live server, the default run stays offline.";
        }
    }
}
