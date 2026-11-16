using FileFerry.Core.Ports;

namespace FileFerry.App.Services;

/// <summary>
/// The real <see cref="IClock"/> from the Composition Root: a thin bridge to the system UTC.
/// The engine and the UI get time only through the port (tests substitute a deterministic clock).
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
