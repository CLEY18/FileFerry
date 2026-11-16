using Android.App;
using Android.Runtime;

namespace FileFerry.App;

/// <summary>
/// Android entry point: the MAUI application. The Dynamic theme is implemented by the
/// <see cref="AndroidThemePalette"/> → <see cref="Theming.ThemeService"/> bridge (reads the
/// system palette and re-maps tokens). Deliberate deviation: <c>DynamicColors.applyToActivities</c>
/// is not applied — it works only with Material3 activity themes, while the UI lives on MAUI
/// controls with tokens; the call would be a no-op.
/// </summary>
[Application]
public sealed class MainApplication : MauiApplication
{
    /// <summary>
    /// JNI-binding constructor (mandatory for the Android runtime).
    /// </summary>
    public MainApplication(nint javaReference, JniHandleOwnership ownership)
        : base(javaReference, ownership)
    {
    }

    /// <summary>
    /// Creates the MauiApp via the shared <see cref="MauiProgram"/>.
    /// </summary>
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
