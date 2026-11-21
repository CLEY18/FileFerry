using FileFerry.App.Services;

namespace FileFerry.App;

/// <summary>
/// Android adapter of <see cref="INotificationPermission"/>: the system dialog via MAUI permissions.
/// Below Android 13 the permission does not exist and the request is a no-op — the silent mode the
/// app was built to tolerate.
/// </summary>
public sealed class AndroidNotificationPermission : INotificationPermission
{
    /// <inheritdoc />
    public async Task RequestAsync()
    {
        try
        {
            await Microsoft.Maui.ApplicationModel.Permissions.RequestAsync<
                Microsoft.Maui.ApplicationModel.Permissions.PostNotifications>();
        }
        catch (Exception)
        {
            // the permission is a UX improvement, not a condition for the engine to work
        }
    }
}
