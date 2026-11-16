using Android.App;
using Android.Content;
using FileFerry.App.Resources;

namespace FileFerry.App;

/// <summary>
/// Notification of the active run for the <c>dataSync</c> FGS (the service lives only for
/// the duration of the transfer). Channel at <see cref="NotificationImportance.Low"/> — no sound,
/// no banner; the text carries only the pair's display-name (secrets cannot be here).
/// </summary>
internal static class SyncForeground
{
    /// <summary>
    /// Stable notification id of the active run (one per app).
    /// </summary>
    internal const int NotificationId = 4711;

    /// <summary>
    /// <c>ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC</c> (0x1, API 29). The constant is marked
    /// obsolete in Mono.Android (the type was removed from the enum in the SDK 36 reference), but
    /// the Android 17 runtime still accepts it for apps holding the FOREGROUND_SERVICE_DATA_SYNC
    /// permission; we keep our own field as a plain number to stay independent of the volatile SDK
    /// API surface.
    /// </summary>
    private const int FgsTypeDataSync = 0x1;

    private const string ChannelId = "fileferry.sync";

    /// <summary>
    /// Info for <c>SetForegroundAsync</c>: the channel is created lazily on the first run.
    /// </summary>
    /// <param name="pairName">Pair display-name from the input data (empty — generic text).</param>
    internal static AndroidX.Work.ForegroundInfo Info(Context context, string? pairName)
    {
        EnsureChannel(context);
        var notification = new Notification.Builder(context, ChannelId)
            .SetContentTitle(AppStrings.SyncForegroundTitle)
            .SetContentText(string.IsNullOrWhiteSpace(pairName) ? AppStrings.SyncForegroundDefaultBody : pairName)
            .SetSmallIcon(Resource.Drawable.fileferry_stat_sync)
            .SetOngoing(true)
            .SetOnlyAlertOnce(true)
            .SetCategory(Notification.CategoryProgress)
            .SetShowWhen(false)
            .Build();
        // Explicit FGS type: the parameterless WorkManager 2.12 constructor sets
        // foregroundServiceType=0 ("none"), and on Android 14+ (SDK 37) startForeground
        // then dies with InvalidForegroundServiceTypeException — a process crash on "Sync now".
        // The value must be a subset of the manifest type (dataSync).
        return new AndroidX.Work.ForegroundInfo(NotificationId, notification, FgsTypeDataSync);
    }

    /// <summary>
    /// Remove the background notification after completion (the FGS itself is already torn down by WorkManager).
    /// </summary>
    internal static void Dismiss(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        manager.Cancel(NotificationId);
    }

    /// <summary>
    /// Creates the Low-importance channel (idempotent; the provider respects user overrides).
    /// </summary>
    private static void EnsureChannel(Context context)
    {
        var manager = (NotificationManager)context.GetSystemService(Context.NotificationService)!;
        if (manager.GetNotificationChannel(ChannelId) is null)
        {
            manager.CreateNotificationChannel(
                new NotificationChannel(ChannelId, AppStrings.SyncChannelName, NotificationImportance.Low));
        }
    }
}
