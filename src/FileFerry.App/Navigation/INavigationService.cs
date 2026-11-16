namespace FileFerry.App.Navigation;

/// <summary>
/// Navigation port inside Shell (declared by its consumer — App/ViewModel, not Core).
/// </summary>
public interface INavigationService
{
    /// <summary>
    /// Navigate to a route with optional parameters.
    /// </summary>
    /// <param name="route">A registered Shell route.</param>
    Task GoAsync(string route, CancellationToken ct);

    /// <summary>
    /// Go one screen back (Shell stack).
    /// </summary>
    Task BackAsync(CancellationToken ct);
}
