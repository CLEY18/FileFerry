using System.Collections.ObjectModel;
using FileFerry.App.Resources;
using FileFerry.App.Services;
using FileFerry.Core.Domain.Paths;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the trash buffer screen: deletion copies from <see cref="IStashRepository"/> + TTL progress
/// from <see cref="IClock"/> and <see cref="StashPolicyOptions"/>. "Restore" = <see cref="StashRestore"/>
/// through <see cref="ISyncScopeFactory"/> (the pair's SAF side): a busy origin is buffered before
/// it is replaced — the deletion rule guards in-app writes too — and a run in flight refuses the
/// restore outright. The restored copy stays in the stash until TTL ends — the safety net never
/// disappears early.
/// </summary>
public sealed class StashViewModel : Mvvm.ObservableObject
{
    private readonly IStashRepository _stash;
    private readonly IClock _clock;
    private readonly IAppDialogs _dialogs;
    private readonly StashPolicyOptions _options;
    private readonly ISyncScopeFactory _scopes;
    private readonly IStashPolicy _stashPolicy;
    private readonly SyncStatusService _status;
    private Guid _pairId;
    private bool _isEmpty = true;
    private bool _isBusy;

    public StashViewModel(
        IStashRepository stash,
        IClock clock,
        IAppDialogs dialogs,
        StashPolicyOptions options,
        ISyncScopeFactory scopes,
        IStashPolicy stashPolicy,
        SyncStatusService status)
    {
        _stash = stash;
        _clock = clock;
        _dialogs = dialogs;
        _options = options;
        _scopes = scopes;
        _stashPolicy = stashPolicy;
        _status = status;

        RestoreCommand = new Mvvm.AsyncRelayCommand(
            async (param, ct) =>
            {
                if (param is StashRow row)
                {
                    await RestoreAsync(row, ct);
                }
            },
            param => param is StashRow { StashPath.Length: > 0 } && !IsBusy);
    }

    /// <summary>
    /// Copy rows (freshest on top — repository order).
    /// </summary>
    public ObservableCollection<StashRow> Items { get; } = [];

    /// <summary>
    /// List is empty → reassuring EmptyState with the TTL promise.
    /// </summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    /// <summary>
    /// "Restore" action: a copy from the stash to the origin, with confirmation.
    /// </summary>
    public Mvvm.AsyncRelayCommand RestoreCommand { get; }

    /// <summary>
    /// A file operation is running — the rest of the controls are blocked.
    /// </summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    /// <summary>
    /// Loads the pair's stash indexes and computes TTL arithmetic as of <c>now</c>.
    /// </summary>
    /// <param name="pairId">Pair taken from the route.</param>
    public async Task LoadAsync(Guid pairId, CancellationToken ct)
    {
        _pairId = pairId;
        try
        {
            var entries = await _stash.ListAsync(pairId, ct);
            var now = _clock.UtcNow;
            var ttl = _options.Ttl;
            var rows = entries
                .Select(entry =>
                {
                    var left = ttl - (now - entry.CreatedAt);
                    var fraction = ttl <= TimeSpan.Zero ? 1 : Math.Clamp(1 - left.Ticks / (double)ttl.Ticks, 0, 1);
                    return new StashRow(
                        entry.OriginPath.ToString(),
                        string.Format(AppStrings.StashDeletedAt, Humanize.Ago(entry.CreatedAt, now)),
                        string.Format(AppStrings.StashExpiresIn, Humanize.ShortLeft(left)),
                        fraction,
                        RestoreCommand)
                    {
                        // payload for "Restore": copy ← stashPath, destination ← originPath
                        StashPath = entry.StashPath.ToString(),
                        OriginPath = entry.OriginPath.ToString(),
                    };
                })
                .ToList();

            await Application.Current!.Dispatcher.DispatchAsync(() =>
            {
                Items.Clear();
                foreach (var row in rows)
                {
                    Items.Add(row);
                }

                IsEmpty = Items.Count == 0;
            });
        }
        catch (OperationCanceledException)
        {
            // the screen was closed while reading
        }
    }

    /// <summary>
    /// "Restore": run-active refusal → confirmation → <see cref="StashRestore"/> through the pair's
    /// SAF side (a busy origin is buffered before it is replaced). Success does not remove the
    /// index: the copy in the stash lives until TTL ends ("TTL progress").
    /// </summary>
    private async Task RestoreAsync(StashRow row, CancellationToken ct)
    {
        if (_status.StatusFor(_pairId) is not null)
        {
            // a run in flight may re-deliver or replace the origin at any moment — restoring
            // beside it would race the transfer for the same file
            await _dialogs.ShowAlertAsync(
                AppStrings.StashRestoreFailedTitle, AppStrings.StashRestoreBusyBody, AppStrings.HelpOk, ct);
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            AppStrings.StashRestore,
            string.Format(AppStrings.StashRestoreConfirmFormat, row.OriginText),
            AppStrings.StashRestore,
            AppStrings.DialogCancel,
            ct);
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var opened = await _scopes.OpenAsync(_pairId, ct).ConfigureAwait(false);
            if (opened.IsFailure)
            {
                await _dialogs.ShowAlertAsync(
                    AppStrings.StashRestoreFailedTitle, Services.ErrorUi.Describe(opened.Error), AppStrings.HelpOk, ct);
                return;
            }

            var copied = await StashRestore.RestoreAsync(
                    opened.Value.Local, _stashPolicy, _clock,
                    RelativePath.Parse(row.StashPath), RelativePath.Parse(row.OriginPath), ct)
                .ConfigureAwait(false);
            if (copied.IsFailure)
            {
                await _dialogs.ShowAlertAsync(
                    AppStrings.StashRestoreFailedTitle, Services.ErrorUi.Describe(copied.Error), AppStrings.HelpOk, ct);
                return;
            }

            await _dialogs.ShowAlertAsync(
                AppStrings.StashRestoreDoneTitle, AppStrings.StashRestoreDoneBody, AppStrings.HelpOk, ct);

            // the restore may have buffered the origin it replaced — the fresh copy belongs
            // on the list at once, not after the next visit
            await LoadAsync(_pairId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // the user may have closed the dialog/screen — the copy is atomic, state stays intact
        }
        finally
        {
            await Application.Current!.Dispatcher.DispatchAsync(() =>
            {
                IsBusy = false;
                RestoreCommand.RaiseCanExecuteChanged();
            });
        }
    }
}
