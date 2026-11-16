using FileFerry.App.Resources;
using FileFerry.Core.Domain;

namespace FileFerry.App.Services;

/// <summary>
/// User-facing error line = a localized label rendered from <see cref="ErrorKind"/> (AppStrings,
/// the render-time phrase pattern) + the raw diagnostic after a middle dot. The label always
/// carries the UI language; the diagnostic is English on purpose (support).
/// An unknown kind (a future enum value on old resources) shows its own key — visible, not fatal.
/// </summary>
public static class ErrorUi
{
    /// <summary>Label in the UI language + raw English diagnostic when one exists.</summary>
    public static string Describe(SyncError error)
    {
        var label = AppStrings.GetByKey(error.PhraseKey(), error.Kind.ToString());
        return string.IsNullOrWhiteSpace(error.Message) ? label : $"{label} · {error.Message}";
    }
}
