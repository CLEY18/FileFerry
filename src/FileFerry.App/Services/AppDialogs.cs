namespace FileFerry.App.Services;

/// <summary>
/// Port for showing a simple dialog (help on the welcome screen, wizard confirmations); declared by the VM layer.
/// </summary>
public interface IAppDialogs
{
    /// <summary>
    /// Show a modal alert; returns after it is dismissed.
    /// </summary>
    Task ShowAlertAsync(string title, string message, string close, CancellationToken ct);

    /// <summary>
    /// Modal confirmation: true — the user pressed "accept" (destructive actions).
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel, CancellationToken ct);

    /// <summary>
    /// Action list (the card's context menu); the selected index or −1 on "Cancel".
    /// </summary>
    Task<int> ShowActionAsync(string title, string cancel, IReadOnlyList<string> options, CancellationToken ct);
}

/// <summary>
/// Adapter of <see cref="IAppDialogs"/> over the current window page's DisplayAlert.
/// A call from a background line (VM commands after
/// <c>ConfigureAwait(false)</c>) builds an AlertDialog without a Looper — a fatal crash.
/// Every show goes through the dispatcher; the adapter does not rely on VM politeness.
/// </summary>
public sealed class MauiAppDialogs : IAppDialogs
{
    /// <inheritdoc />
    public Task ShowAlertAsync(string title, string message, string close, CancellationToken ct) =>
        OnMainThreadAsync(async () =>
        {
            var page = CurrentPage();
            if (page is not null)
            {
                await page.DisplayAlertAsync(title, message, close);
            }
        });

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel, CancellationToken ct) =>
        OnMainThreadAsync(async () =>
        {
            var page = CurrentPage();
            return page is not null && await page.DisplayAlertAsync(title, message, accept, cancel);
        });

    /// <inheritdoc />
    public Task<int> ShowActionAsync(string title, string cancel, IReadOnlyList<string> options, CancellationToken ct) =>
        OnMainThreadAsync(async () =>
        {
            var page = CurrentPage();
            if (page is null)
            {
                return -1;
            }

            var buttons = options.ToArray();
            var choice = await page.DisplayActionSheetAsync(title, cancel, null, buttons);
            return choice is null ? -1 : Array.IndexOf(buttons, choice);
        });

    /// <summary>
    /// Dispatch to the UI thread; with no running app — the "cancelled" default (like an empty page).
    /// </summary>
    private static Task OnMainThreadAsync(Func<Task> onMain)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null ? Task.CompletedTask : dispatcher.DispatchAsync(onMain);
    }

    /// <inheritdoc cref="OnMainThreadAsync(Func{Task})"/>
    private static Task<T> OnMainThreadAsync<T>(Func<Task<T>> onMain)
    {
        var dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null
            ? Task.FromResult(default(T)!)
            : dispatcher.DispatchAsync(onMain);
    }

    /// <summary>
    /// The current window's page whose activity draws the dialog.
    /// </summary>
    private static Page? CurrentPage() => Application.Current?.Windows.FirstOrDefault()?.Page;
}
