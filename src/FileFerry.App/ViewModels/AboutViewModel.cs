using FileFerry.App.Resources;
using FileFerry.App.Services;
using Microsoft.Maui.Storage;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the about screen: mark, version from <c>AppInfo</c>, tagline, the mandatory
/// Nextcloud GmbH disclaimer (wording, rendered from the string resources like any other label)
/// and three links. The docs list no release URLs — links stay honest until canonical
/// addresses appear (constants get consolidated at release).
/// </summary>
public sealed class AboutViewModel : Mvvm.ObservableObject
{
    private readonly IAppDialogs _dialogs;

    /// <summary>
    /// Creates the About screen VM from dialogs; the link commands are stubs until release.
    /// </summary>
    public AboutViewModel(IAppDialogs dialogs)
    {
        _dialogs = dialogs;
        SourceCommand = Pending(AppStrings.AboutSource);
        BugsCommand = Pending(AppStrings.AboutBugs);
        PrivacyCommand = Pending(AppStrings.PrivacyTitle);
    }

    /// <summary>
    /// Build version (Android versionName) per the microcopy.
    /// </summary>
    public string VersionLine { get; } = string.Format(AppStrings.AboutVersion, AppInfo.Current.VersionString);

    public Mvvm.AsyncRelayCommand SourceCommand { get; }

    public Mvvm.AsyncRelayCommand BugsCommand { get; }

    public Mvvm.AsyncRelayCommand PrivacyCommand { get; }

    /// <summary>
    /// Link stub: shows the same "under development" dialog, no fabricated URLs.
    /// </summary>
    private Mvvm.AsyncRelayCommand Pending(string feature) => new(
        (_, ct) => _dialogs.ShowAlertAsync(
            AppStrings.PendingSectionTitle,
            string.Format(AppStrings.PendingSectionBody, feature),
            AppStrings.HelpOk,
            ct));
}
