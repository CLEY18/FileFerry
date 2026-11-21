namespace FileFerry.App.Services;

/// <summary>
/// Port for the system notification-permission ask: declared by its consumer (the wizard asks right
/// after a pair is saved — the moment a background transfer exists to report on). Deliberately
/// policy-free: whether a run shows a notification is the scheduler's question, not this port's.
/// </summary>
public interface INotificationPermission
{
    /// <summary>
    /// Ask the system once; a refusal is harmless (the app syncs without transfer notifications)
    /// and any platform hiccup is swallowed inside the adapter.
    /// </summary>
    Task RequestAsync();
}
