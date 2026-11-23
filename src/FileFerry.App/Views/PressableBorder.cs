namespace FileFerry.App.Views;

/// <summary>
/// Border used as a tappable card/row/chip: it never sits below the Android touch target, and
/// on Android the platform's pressed highlight is mounted on its container by the platform
/// handler — a plain Border with a tap gesture gives no touch feedback of its own.
/// </summary>
public sealed class PressableBorder : Border
{
    public PressableBorder()
    {
        MinimumHeightRequest = DesignTokens.TouchTargetAndroid;
    }
}
