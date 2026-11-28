using FileFerry.App.Resources;

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
    /// A destructive confirmation wears the platform dialog with the accept in the error tone;
    /// an ordinary one keeps the standard presentation.
    /// </summary>
    Task<bool> ConfirmAsync(string title, string message, string accept, string cancel, CancellationToken ct, bool destructive = false);

    /// <summary>
    /// Action list (the card's context menu); the selected index or −1 on "Cancel".
    /// </summary>
    Task<int> ShowActionAsync(string title, string cancel, IReadOnlyList<string> options, CancellationToken ct);

    /// <summary>
    /// Single-choice dialog with the current option marked (an exclusive setting — the user must
    /// see where they are before choosing); the chosen index or −1 on cancel/dismiss.
    /// </summary>
    Task<int> ShowChoiceAsync(string title, IReadOnlyList<string> options, int selectedIndex, CancellationToken ct);

    /// <summary>
    /// A transient result notice (a snackbar): for outcomes the user does not have to confirm;
    /// returns once the notice is on screen.
    /// </summary>
    Task NotifyAsync(string message, CancellationToken ct);
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
    public Task ShowAlertAsync(string title, string message, string close, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask; // the requesting screen is gone — nothing to interrupt
        }

        return OnMainThreadAsync(async () =>
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            var page = CurrentPage();
            if (page is not null)
            {
                await page.DisplayAlertAsync(title, message, close);
            }
        });
    }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel, CancellationToken ct, bool destructive = false)
    {
        if (!destructive)
        {
            if (ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            return OnMainThreadAsync(async () =>
            {
                var page = CurrentPage();
                return page is not null
                    && !ct.IsCancellationRequested
                    && await page.DisplayAlertAsync(title, message, accept, cancel);
            });
        }
#if ANDROID
        // a destructive confirmation goes to the platform dialog so its accept carries the
        // error tone - the caution reads before the tap, not after
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(false);
        }

        return OnMainThreadAsync(() =>
        {
            var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (context is null || ct.IsCancellationRequested)
            {
                return Task.FromResult(false);
            }

            var completion = new TaskCompletionSource<bool>();
            var builder = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(context);
            builder.SetTitle(title);
            builder.SetMessage(message);
            builder.SetPositiveButton(accept, (_, _) => completion.TrySetResult(true));
            builder.SetNegativeButton(cancel, (_, _) => completion.TrySetResult(false));
            builder.SetOnCancelListener(new DismissNotice(() => completion.TrySetResult(false)));
            var dialog = builder.Create();
            dialog.Show();
            if (TokenArgb("StatusError") is { } error)
            {
                // the framework numbers its dialog buttons; the positive (accept) slot is the first
                dialog.GetButton(-1)?
                    .SetTextColor(new Android.Graphics.Color(error));
            }

            DismissOnCancellation(ct, completion, () => dialog.Dismiss());
            return completion.Task;
        });
#else
        return OnMainThreadAsync(async () =>
        {
            var page = CurrentPage();
            return page is not null && await page.DisplayAlertAsync(title, message, accept, cancel);
        });
#endif
    }

    /// <inheritdoc />
    public Task<int> ShowActionAsync(string title, string cancel, IReadOnlyList<string> options, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(-1);
        }

        return OnMainThreadAsync(async () =>
        {
            var page = CurrentPage();
            if (page is null || ct.IsCancellationRequested)
            {
                return -1;
            }

            var buttons = options.ToArray();
            var choice = await page.DisplayActionSheetAsync(title, cancel, null, buttons);
            return choice is null ? -1 : Array.IndexOf(buttons, choice);
        });
    }

    /// <inheritdoc />
    public Task<int> ShowChoiceAsync(string title, IReadOnlyList<string> options, int selectedIndex, CancellationToken ct)
    {
#if ANDROID
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(-1);
        }

        return OnMainThreadAsync(() =>
        {
            var context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (context is null || ct.IsCancellationRequested)
            {
                return Task.FromResult(-1);
            }

            // the first completed path wins: a tapped row sets its index before the dismiss
            // reaches the cancel listener, a bare dismiss or the cancel button lands -1
            // (the builder mutates itself on every call, so the steps run as statements)
            var completion = new TaskCompletionSource<int>();
            var builder = new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(context);
            builder.SetTitle(title);
            builder.SetSingleChoiceItems(
                options.ToArray(),
                selectedIndex,
                new ChoiceRowClick((source, which) =>
                {
                    completion.TrySetResult(which);
                    source?.Dismiss();
                }));
            builder.SetNegativeButton(AppStrings.DialogCancel, (_, _) => completion.TrySetResult(-1));
            builder.SetOnCancelListener(new DismissNotice(() => completion.TrySetResult(-1)));
            var dialog = builder.Create();
            dialog.Show();
            DismissOnCancellation(ct, completion, () => dialog.Dismiss(), -1);
            return completion.Task;
        });
#else
        // no platform radio dialog on other targets — the plain action list still answers, only
        // without the mark of the current value (the app ships for Android; this keeps the shared code sound)
        return ShowActionAsync(title, AppStrings.DialogCancel, options, ct);
#endif
    }

    /// <inheritdoc />
    public Task NotifyAsync(string message, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.CompletedTask; // the result belongs to a screen the user already left
        }

#if ANDROID
        return OnMainThreadAsync(() =>
        {
            if (ct.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            var decor = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity?.Window?.DecorView;
            if (decor is null)
            {
                return Task.CompletedTask;
            }

            var notice = Google.Android.Material.Snackbar.Snackbar.Make(
                decor, message, Google.Android.Material.Snackbar.Snackbar.LengthLong);
            // the notice wears the app's inverted pair of tones (the text color as the ground,
            // the page color as the ink), so it reads as the app's own surface, not the framework's
            if (TokenArgb("TextPrimary") is { } ground && TokenArgb("Background") is { } ink)
            {
                notice.View?.SetBackgroundColor(new Android.Graphics.Color(ground));
                notice.SetTextColor(new Android.Graphics.Color(ink));
            }

            notice.Show();
            return Task.CompletedTask;
        });
#else
        return Task.CompletedTask;
#endif
    }

#if ANDROID
    /// <summary>
    /// Listener bridges for the platform dialog: Java callbacks as the small delegates this code needs.
    /// </summary>
    private sealed class ChoiceRowClick(Action<Android.Content.IDialogInterface?, int> onClick) : Java.Lang.Object, Android.Content.IDialogInterfaceOnClickListener
    {
        public void OnClick(Android.Content.IDialogInterface? dialog, int which) => onClick(dialog, which);
    }

    private sealed class DismissNotice(Action onCancel) : Java.Lang.Object, Android.Content.IDialogInterfaceOnCancelListener
    {
        public void OnCancel(Android.Content.IDialogInterface? dialog) => onCancel();
    }

    /// <summary>
    /// A dialog whose requesting screen vanished is dismissed by the caller's token:
    /// the open result falls to the dismissal default and the dialog leaves whatever
    /// screen the user navigated to in the meantime.
    /// </summary>
    private static void DismissOnCancellation<T>(
        CancellationToken ct, TaskCompletionSource<T> completion, Action dismiss, T? dismissed = default)
    {
        if (!ct.CanBeCanceled)
        {
            return; // nothing to watch: the dialog answers on its own
        }

        var registration = ct.Register(() =>
        {
            completion.TrySetResult(dismissed!);
            Application.Current?.Dispatcher.Dispatch(dismiss);
        });
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(), TaskScheduler.Default);
    }

    /// <summary>
    /// A color token resolved to an ARGB int through the live application resources
    /// (the theme service rewrites these values; a notice shown now wears the current palette).
    /// </summary>
    private static int? TokenArgb(string key) =>
        Application.Current?.Resources.TryGetValue(key, out var resource) is true
        && resource is Microsoft.Maui.Graphics.Color color
            ? unchecked((int)color.ToUint())
            : null;
#endif

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
