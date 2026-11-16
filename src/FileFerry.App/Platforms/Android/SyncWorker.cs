using Android.Content;
using AndroidX.Work;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace FileFerry.App;

/// <summary>
/// Thin worker ("Worker without policies"): takes the pair from the input data, gets
/// <see cref="SyncRun"/> from DI and maps its result onto WorkManager results.
/// There is no sync logic here at all; a WorkManager stop is turned into a
/// <see cref="CancellationToken"/> all the way down to HTTP. The body is a narrow bridge
/// from the synchronous <c>doWork()</c> binding onto the async pipeline (runs on a WorkManager pool thread).
/// </summary>
public sealed class SyncWorker : Worker
{
    /// <summary>
    /// Key of the pair id in the worker's input data.
    /// </summary>
    public const string PairIdKey = "pair_id";

    /// <summary>
    /// Key of the pair display-name (notification only — non-sensitive data).
    /// </summary>
    public const string PairNameKey = "pair_name";

    private readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// Constructor for WorkManager's reflective creation (mandatory signature).
    /// </summary>
    public SyncWorker(Context context, WorkerParameters parameters)
        : base(context, parameters)
    {
    }

    /// <summary>
    /// Synchronous entry of the binding: blocking await of the async run (this is a WorkManager background thread).
    /// </summary>
    public override ListenableWorker.Result DoWork()
    {
        try
        {
            return RunAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // scheduler/UI stop — the run journal is already consistent, no retry needed
            return ListenableWorker.Result.InvokeSuccess()!;
        }
    }

    /// <summary>
    /// Stop from outside (pause/stopWork): translated into cancellation of the run.
    /// </summary>
    public override void OnStopped()
    {
        base.OnStopped();
        _stop.Cancel();
    }

    /// <summary>
    /// The Worker is IDisposable in the binding: release the cancellation source.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stop.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Body: dataSync FGS (best-effort) → one <see cref="SyncRun"/> → WorkManager result.
    /// </summary>
    private async Task<ListenableWorker.Result> RunAsync()
    {
        var context = ApplicationContext;
        var provider = MauiProgram.Application?.Services;
        if (provider is null || InputData?.GetString(PairIdKey) is not { Length: > 0 } raw)
        {
            // the app is not initialized yet / no more — a working situation, not a data fault
            return ListenableWorker.Result.InvokeRetry()!;
        }

        var pairId = Guid.Parse(raw);
        var pairName = InputData?.GetString(PairNameKey);
        TryEnterForeground(context, pairName);

        var run = provider.GetRequiredService<SyncRun>();
        var status = provider.GetRequiredService<Services.SyncStatusService>();
        using var registration = status.Begin(pairId); // Worker→UI hub (#3)
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, registration.Token);
        var result = await run.RunAsync(pairId, linked.Token).ConfigureAwait(false);

        if (context is not null)
        {
            SyncForeground.Dismiss(context);
        }

        return result.IsSuccess
            ? ListenableWorker.Result.InvokeSuccess()!
            : result.Error.IsTransient ? ListenableWorker.Result.InvokeRetry()! : ListenableWorker.Result.InvokeFailure()!;
    }

    /// <summary>
    /// Hands the FGS notification to the worker; a system refusal (missing permission) does not cancel the run itself.
    /// </summary>
    private void TryEnterForeground(Context? context, string? pairName)
    {
        if (context is null)
        {
            return;
        }

        try
        {
            // the binding returns a Java future (not a Task): it is dispatched to the main thread,
            // and we block our own worker thread in DoWork() — there is nothing to await here
            SetForegroundAsync(SyncForeground.Info(context, pairName));
        }
        catch (Exception)
        {
            // the run stays valid in the background even without FGS — the notification is only honesty with the user
        }
    }
}
