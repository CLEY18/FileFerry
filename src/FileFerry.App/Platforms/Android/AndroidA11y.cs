using AndroidView = Android.Views.View;

namespace FileFerry.App;

/// <summary>
/// Attached flag: the view is pure decoration (an icon-font glyph beside its text) and a screen
/// reader must skip it — a private-use codepoint reads as gibberish otherwise. The flag applies
/// when the platform view exists; a view mounted later gets it on handler attach.
/// </summary>
public static class A11y
{
    public static readonly BindableProperty IsDecorativeProperty = BindableProperty.CreateAttached(
        "IsDecorative", typeof(bool), typeof(A11y), false,
        propertyChanged: (bindable, _, value) =>
        {
            if (value is true && bindable is VisualElement element)
            {
                HideWhenReady(element);
            }
        });

    public static bool GetIsDecorative(BindableObject view) => (bool)view.GetValue(IsDecorativeProperty);

    public static void SetIsDecorative(BindableObject view, bool value) => view.SetValue(IsDecorativeProperty, value);

    private static void HideWhenReady(VisualElement element)
    {
        if (element.Handler?.PlatformView is AndroidView platform)
        {
            Hide(platform);
            return;
        }

        element.HandlerChanged += (_, _) =>
        {
            if (element.Handler?.PlatformView is AndroidView ready)
            {
                Hide(ready);
            }
        };
    }

    private static void Hide(AndroidView platform) =>
        platform.ImportantForAccessibility = Android.Views.ImportantForAccessibility.NoHideDescendants;
}
