using Android.Views;
using Microsoft.Maui.ApplicationModel;

namespace FileFerry.App;

/// <summary>
/// Marks the window as carrying private values (no screenshots, no preview in the recent-apps
/// switcher) while the sign-in screen is on it, and clears the mark on leaving. The flag belongs to
/// the window, so it is set and cleared by the screen that owns the secret moment, not by the app.
/// </summary>
public static class AndroidSecureScreen
{
    public static void SetSecure(bool secure)
    {
        if (Platform.CurrentActivity?.Window is not { } window)
        {
            return;
        }

        if (secure)
        {
            window.AddFlags(WindowManagerFlags.Secure);
        }
        else
        {
            window.ClearFlags(WindowManagerFlags.Secure);
        }
    }
}
