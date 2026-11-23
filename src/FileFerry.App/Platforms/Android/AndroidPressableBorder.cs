using Android.Content;
using Android.Util;
using Android.Views;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace FileFerry.App;

/// <summary>
/// Android handler for the pressable rows/chips: mounts the theme's own pressed highlight
/// (the selectable-item background) on the container and creates it as a view that mirrors
/// the pressed state itself — the framework gesture pipeline consumes the touch before a
/// plain container would mark itself pressed, so the highlight would never draw.
/// </summary>
public sealed class PressableBorderHandler : BorderHandler
{
    protected override ContentViewGroup CreatePlatformView()
        => new HighlightContainerView(Context!);

    private sealed class HighlightContainerView : ContentViewGroup
    {
        public HighlightContainerView(Context context)
            : base(context)
        {
            MountHighlight(context);
        }

        public override bool DispatchTouchEvent(MotionEvent? e)
        {
            switch (e?.ActionMasked)
            {
                case MotionEventActions.Down:
                    Pressed = true;
                    break;
                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    Pressed = false;
                    break;
            }

            return base.DispatchTouchEvent(e);
        }

        private void MountHighlight(Context context)
        {
            if (context.Theme is not { } theme || context.Resources is not { } resources)
            {
                return;
            }

            var value = new TypedValue();
            if (!theme.ResolveAttribute(Android.Resource.Attribute.SelectableItemBackground, value, true)
                || value.ResourceId == 0)
            {
                return;
            }

            Foreground = resources.GetDrawable(value.ResourceId, theme);
            // the highlight's state list and keyboard focus need a clickable, focusable host
            Clickable = true;
            Focusable = true;
        }
    }
}
