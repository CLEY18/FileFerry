using Android.Util;
using AndroidView = Android.Views.View;

namespace FileFerry.App.Views;

/// <summary>
/// Border used as a segment chip: it never sits below the Android touch target, and the platform's
/// pressed highlight (the theme's selectable-item background) is mounted under it so a tap visibly
/// registers — a plain Border with a tap gesture gives no touch feedback of its own.
/// </summary>
public sealed class PressableBorder : Border
{
    public PressableBorder()
    {
        MinimumHeightRequest = DesignTokens.TouchTargetAndroid;
        HandlerChanged += (_, _) =>
        {
            if (Handler?.PlatformView is AndroidView platform)
            {
                MountRipple(platform);
            }
        };
    }

    private static void MountRipple(AndroidView platform)
    {
        if (platform.Context?.Theme is not { } theme || platform.Resources is not { } resources)
        {
            return;
        }

        var value = new TypedValue();
        if (!theme.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, value, true)
            || value.ResourceId == 0)
        {
            return;
        }

        platform.Foreground = resources.GetDrawable(value.ResourceId, theme);
        // the pressed state the highlight draws needs a clickable host
        platform.Clickable = true;
    }
}
