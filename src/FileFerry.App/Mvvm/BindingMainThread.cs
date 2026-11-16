namespace FileFerry.App.Mvvm;

/// <summary>
/// Marshals binding events onto the UI thread. Without it, <c>PropertyChanged</c>/
/// <c>CanExecuteChanged</c> after <c>ConfigureAwait(false)</c> arrive from a pool thread,
/// and MAUI updates the native Control from there — a crash of the "Animators may only be
/// run on Looper threads" class (RippleDrawable on Android). This is an infrastructure
/// guarantee of the MVVM layer: no VM should have to remember the dispatcher for binding events.
/// </summary>
internal static class BindingMainThread
{
    /// <summary>
    /// Run <paramref name="action"/> on the UI thread. With no running <c>Application</c>
    /// (unit tests, early startup) or when already on the UI thread — run synchronously,
    /// preserving order.
    /// </summary>
    public static void Post(Action action)
    {
        var dispatcher = Microsoft.Maui.Controls.Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.IsDispatchRequired)
        {
            action();
            return;
        }

        dispatcher.Dispatch(action);
    }
}
