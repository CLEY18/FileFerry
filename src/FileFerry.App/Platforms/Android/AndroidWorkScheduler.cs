using AndroidX.Work;
using FileFerry.Core.Domain;
using FileFerry.Core.Ports;

namespace FileFerry.App;

/// <summary>
/// Android adapter for <see cref="ISyncScheduler"/> on WorkManager: a unique-periodic job
/// per pair ("one pair — one active job" is realized by the unique name + UPDATE itself), an
/// <c>UNMETERED</c> Constraint for wifi-only pairs, a one-time "Now" with REPLACE — marked as the
/// owner's direct order, so it runs even a paused pair.
/// The <see cref="SyncSchedule.Manual"/> interval and pause remove the job.
/// </summary>
public sealed class AndroidWorkScheduler : ISyncScheduler
{
    private readonly IFolderPairRepository _pairs;

    /// <summary>
    /// Creates the scheduler over the pair configuration (constraints are read from the pair).
    /// </summary>
    public AndroidWorkScheduler(IFolderPairRepository pairs) => _pairs = pairs;

    private static WorkManager Manager => WorkManager.GetInstance(Android.App.Application.Context);

    /// <inheritdoc />
    public async Task ScheduleAsync(Guid pairId, SyncSchedule schedule, CancellationToken ct)
    {
        var pair = await _pairs.FindAsync(pairId, ct).ConfigureAwait(false);
        if (pair is null || pair.Paused || schedule == SyncSchedule.Manual)
        {
            await CancelAsync(pairId, ct).ConfigureAwait(false);
            return;
        }

        var interval = TimeSpan.FromMinutes((int)schedule);
        var request = new PeriodicWorkRequest.Builder(typeof(SyncWorker), interval)
            .SetInputData(Input(pairId, pair.DisplayName))
            .SetConstraints(Constraints(pair))
            .Build();
        Manager.EnqueueUniquePeriodicWork(PeriodicName(pairId), ExistingPeriodicWorkPolicy.Update!, request);
    }

    /// <inheritdoc />
    public async Task RunNowAsync(Guid pairId, CancellationToken ct)
    {
        var pair = await _pairs.FindAsync(pairId, ct).ConfigureAwait(false);
        if (pair is null)
        {
            return;
        }

        var request = new OneTimeWorkRequest.Builder(typeof(SyncWorker))
            .SetInputData(Input(pairId, pair.DisplayName, runNow: true))
            .SetConstraints(Constraints(pair))
            .Build();
        Manager.EnqueueUniqueWork(OneTimeName(pairId), ExistingWorkPolicy.Replace!, request);
    }

    /// <inheritdoc />
    public Task CancelAsync(Guid pairId, CancellationToken ct)
    {
        Manager.CancelUniqueWork(PeriodicName(pairId));
        Manager.CancelUniqueWork(OneTimeName(pairId));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Worker data: pair id + display-name for the notification (no secrets whatsoever).
    /// </summary>
    private static AndroidX.Work.Data Input(Guid pairId, string displayName, bool runNow = false) =>
        new AndroidX.Work.Data.Builder()
            .PutString(SyncWorker.PairIdKey, pairId.ToString("D"))
            .PutString(SyncWorker.PairNameKey, displayName)
            .PutBoolean(SyncWorker.RunNowKey, runNow)
            .Build();

    /// <summary>
    /// A wifi-only pair waits for an unmetered network; the rest — any connectivity.
    /// </summary>
    private static Constraints Constraints(FolderPair pair) =>
        new Constraints.Builder()
            .SetRequiredNetworkType((pair.WifiOnly ? NetworkType.Unmetered : NetworkType.Connected)!)
            .Build();

    private static string PeriodicName(Guid pairId) => $"sync-pair-{pairId:N}";

    private static string OneTimeName(Guid pairId) => $"sync-now-{pairId:N}";
}
