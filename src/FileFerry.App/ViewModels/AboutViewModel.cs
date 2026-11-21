using FileFerry.App.Resources;
using FileFerry.App.Services;
using Microsoft.Maui.Storage;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the about screen: mark, version from <c>AppInfo</c>, tagline, the mandatory
/// Nextcloud GmbH disclaimer (wording, rendered from the string resources like any other label)
/// and three links. Source code and bug report open the project's public repository in the
/// system browser; the privacy policy has no canonical address yet, so that link stays honest.
/// </summary>
public sealed class AboutViewModel : Mvvm.ObservableObject
{
    /// <summary>
    /// Public source repository; the issue tracker is its <c>/issues</c> section.
    /// </summary>
    private const string SourceUrl = "https://github.com/CLEY18/FileFerry";
    private const string IssuesUrl = SourceUrl + "/issues";

    private readonly IAppDialogs _dialogs;

    /// <summary>
    /// Creates the About screen VM from dialogs; source and bug links launch the browser,
    /// the privacy link is a stub until its address exists.
    /// </summary>
    public AboutViewModel(IAppDialogs dialogs)
    {
        _dialogs = dialogs;
        SourceCommand = Link(SourceUrl);
        BugsCommand = Link(IssuesUrl);
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
    /// Link command: hands the address to the system browser.
    /// </summary>
    private static Mvvm.AsyncRelayCommand Link(string url) => new(
        async (_, _) => await Browser.Default.OpenAsync(new Uri(url)));

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
