using Android.Views;
using FileFerry.App.Theming;
using Microsoft.Maui.ApplicationModel;
using AppBarLayout = Google.Android.Material.AppBar.AppBarLayout;
using MauiApplication = Microsoft.Maui.Controls.Application;
using SystemColor = Microsoft.Maui.Graphics.Color;
using Toolbar = AndroidX.AppCompat.Widget.Toolbar;

namespace FileFerry.App;

/// <summary>
/// Paints the live Shell app bar (and the status strip above it) from tokens. The MAUI toolbar
/// is not part of the resource tree, so a token change cannot reach it by itself: the theme
/// service's "tokens re-mapped" event drives this repaint, and a navigation re-paint is queued
/// after MAUI's own pass because that pass re-applies the framework default appearance.
/// Title and icons are token-bound through the Shell's own chrome colors — this covers only
/// the surfaces Shell leaves unpainted.
/// </summary>
public static class AndroidAppBar
{
    private static bool attached;

    /// <summary>
    /// Subscribe the repaint to the theme service (once; fresh toolbars are painted on navigation).
    /// </summary>
    public static void Attach(IThemeService theme)
    {
        if (attached)
        {
            return;
        }

        attached = true;
        theme.Applied += (_, _) => Repaint();
    }

    /// <summary>
    /// Repaint once MAUI has finished its own pass for a navigation (the queue order makes the
    /// token paint the later one, so it survives the framework's default re-apply).
    /// </summary>
    public static void RepaintAfterNavigation()
    {
        if (Platform.CurrentActivity?.Window?.DecorView is { } decor)
        {
            decor.Post(Repaint);
        }
        else
        {
            Repaint();
        }
    }

    /// <summary>
    /// Re-resolve the app-bar background token onto every toolbar container the window hosts,
    /// including the strip the layout paints behind the system status bar. A no-op before the
    /// toolbar exists; a later navigation or event paints it. The window surface behind the page
    /// content (the strip below it, around the system navigation bar) is painted from the page
    /// background token as well — the theme swap never recreates the activity, so the platform
    /// window background keeps the tone of the previous theme unless it is re-painted here.
    /// </summary>
    public static void Repaint()
    {
        if (MauiApplication.Current?.Resources["Primary"] is not SystemColor brand
            || Platform.CurrentActivity?.Window is not { } window
            || window.DecorView is not ViewGroup decor)
        {
            return;
        }

        if (MauiApplication.Current.Resources["Background"] is SystemColor page)
        {
            decor.SetBackgroundColor(ToAndroidColor(page));
        }

        var bg = ToAndroidColor(brand);
        Paint(decor);

        // under edge-to-edge the strip above the app bar is the layout's status-bar foreground,
        // and the icon polarity follows the painted surface so both stay readable
        AndroidX.Core.View.WindowCompat.GetInsetsController(window, decor)?.AppearanceLightStatusBars =
            IsLight(brand);

        void Paint(Android.Views.View view)
        {
            switch (view)
            {
                case Toolbar toolbar:
                    toolbar.SetBackgroundColor(bg);
                    // the framework default stacks a 16dp content inset on top of the navigation
                    // button before the title starts; the design gap is the button edge itself
                    toolbar.ContentInsetStartWithNavigation = 0;
                    break;
                case AppBarLayout bar:
                    bar.SetBackgroundColor(bg);
                    bar.SetStatusBarForegroundColor(bg);
                    break;
            }

            if (view is ViewGroup group)
            {
                for (var i = 0; i < group.ChildCount; i++)
                {
                    if (group.GetChildAt(i) is { } child)
                    {
                        Paint(child);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Token color → platform color: components are sRGB 0..1 in the resource tree.
    /// </summary>
    private static Android.Graphics.Color ToAndroidColor(SystemColor color) => new(
        (int)Math.Round(color.Red * 255),
        (int)Math.Round(color.Green * 255),
        (int)Math.Round(color.Blue * 255),
        (int)Math.Round(color.Alpha * 255));

    private static bool IsLight(SystemColor color) =>
        color.Red * 0.299 + color.Green * 0.587 + color.Blue * 0.114 > 0.6;
}
