using Android.Content.Res;
using Android.Util;
using AndroidView = Android.Views.View;

namespace FileFerry.App;

/// <summary>
/// Attached to the transparent Button that carries a card or row tap: the platform's own
/// highlight for pressed/focus states is mounted as the button's background tint (a plain
/// transparent background would erase it), with the Material insets and lift removed so the
/// highlight covers the surface exactly. Without the flag a Button behaves as always.
/// </summary>
public static class PressRipple
{
    public static readonly BindableProperty EnabledProperty = BindableProperty.CreateAttached(
        "Enabled", typeof(bool), typeof(PressRipple), false,
        propertyChanged: (bindable, _, value) =>
        {
            if (value is true && bindable is VisualElement element)
            {
                ApplyWhenReady(element);
            }
        });

    public static bool GetEnabled(BindableObject view) => (bool)view.GetValue(EnabledProperty);

    public static void SetEnabled(BindableObject view, bool value) => view.SetValue(EnabledProperty, value);

    private static void ApplyWhenReady(VisualElement element)
    {
        if (element.Handler?.PlatformView is AndroidView platform)
        {
            Apply(platform);
            return;
        }

        element.HandlerChanged += (_, _) =>
        {
            if (element.Handler?.PlatformView is AndroidView ready)
            {
                Apply(ready);
            }
        };
    }

    private static void Apply(AndroidView platform)
    {
        if (platform is not Google.Android.Material.Button.MaterialButton)
        {
            return;
        }

        // after the current layout/mapping pass: the framework's own background
        // mapping for the control runs first, its transparent tint must not win over this one
        platform.Post(() => ApplyTint(platform));
    }

    private static void ApplyTint(AndroidView platform)
    {
        var context = platform.Context;
        if (context is null || platform is not Google.Android.Material.Button.MaterialButton material)
        {
            return;
        }

        var highlight = HighlightColor(context, platform.Resources);
        var states = new[]
        {
            new[] { Android.Resource.Attribute.StatePressed },
            new[] { Android.Resource.Attribute.StateFocused },
            new int[0],
        };
        material.BackgroundTintList = new ColorStateList(
            states,
            [highlight, highlight, Android.Graphics.Color.Transparent]);
        material.InsetTop = 0;
        material.InsetBottom = 0;
        material.Elevation = 0f;
        material.StateListAnimator = null;
    }

    /// <summary>
    /// The theme's own control-highlight color (white-based in dark, black-based in light),
    /// with the Material state-layer constant as the fallback.
    /// </summary>
    private static int HighlightColor(Android.Content.Context context, Android.Content.Res.Resources? resources)
    {
        var value = new TypedValue();
        try
        {
            if (context.Theme?.ResolveAttribute(Android.Resource.Attribute.ColorControlHighlight, value, true) == true)
            {
                if (value.ResourceId != 0 && resources is not null)
                {
                    return resources.GetColor(value.ResourceId, context.Theme);
                }

                if (value.Type is not (DataType.Null or DataType.Reference))
                {
                    return value.Data;
                }
            }
        }
        catch (Android.Content.Res.Resources.NotFoundException)
        {
            // a non-standard theme: the constant below is the Material state-layer default
        }

        return Android.Graphics.Color.ParseColor("#1F000000");
    }
}
