using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using FileFerry.App.Services;

namespace FileFerry.App;

/// <summary>
/// Android adapter for <see cref="IBatteryOptimization"/>: state via <c>PowerManager</c>,
/// request via <c>ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS</c> (declared in the manifest),
/// with a fallback to the <c>ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS</c> screen — some ROMs
/// block the direct dialog (an expected boundary, not a bug).
/// </summary>
public sealed class AndroidBatteryOptimization : IBatteryOptimization
{
    /// <inheritdoc />
    public bool IsIgnored
    {
        get
        {
            var context = Android.App.Application.Context;
            if (context.GetSystemService(Context.PowerService) is not PowerManager power)
            {
                return false; // a run without PowerManager does not happen — at most in a broken test environment
            }

            return power.IsIgnoringBatteryOptimizations(context.PackageName ?? string.Empty);
        }
    }

    /// <inheritdoc />
    public Task OpenRequestAsync(CancellationToken ct)
    {
        var context = Android.App.Application.Context;
        var packageUri = Android.Net.Uri.Parse($"package:{context.PackageName}");
        try
        {
            Start(context, new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, packageUri));
        }
        catch (Java.Lang.Exception)
        {
            // the ROM rejected the direct dialog (SecurityException/ActivityNotFound) — route to the settings screen
            Start(context, new Intent(Settings.ActionIgnoreBatteryOptimizationSettings));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starting from a non-Activity context requires NEW_TASK — a platform requirement.
    /// </summary>
    private static void Start(Context context, Intent intent)
    {
        intent.AddCategory(Intent.CategoryLauncher);
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
    }
}
