namespace FileFerry.App.Navigation;

/// <summary>
/// <see cref="INavigationService"/> adapter over the Shell stack: a thin delegate to
/// Shell navigation with no policy at all (it decides only "where", never "whether" —
/// that is the other layers' job).
/// </summary>
public sealed class ShellNavigationService : INavigationService
{
    /// <inheritdoc />
    public Task GoAsync(string route, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ct.ThrowIfCancellationRequested();
        // Shell.GoToAsync takes no token — cancellation is checked before the transition animation starts.
        return Shell.Current.GoToAsync(route);
    }

    /// <inheritdoc />
    public Task BackAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Shell.Current.GoToAsync("..");
    }
}
