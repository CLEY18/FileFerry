using FileFerry.App.Navigation;
using FileFerry.App.Resources;
using Microsoft.Maui.Storage;

namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the about screen: mark, version from <c>AppInfo</c>, tagline, the mandatory
/// Nextcloud GmbH disclaimer (wording, rendered from the string resources like any other label)
/// and three links. Source code and bug report open the project's public repository in the
/// system browser; privacy opens the in-app summary screen.
/// </summary>
public sealed class AboutViewModel : Mvvm.ObservableObject
{
    /// <summary>
    /// Public source repository; the issue tracker is its <c>/issues</c> section.
    /// </summary>
    private const string SourceUrl = "https://github.com/CLEY18/FileFerry";
    private const string IssuesUrl = SourceUrl + "/issues";

    private readonly INavigationService _navigation;

    /// <summary>
    /// Creates the About screen VM from the navigation port; source and bug links launch the
    /// browser, the privacy link opens the privacy screen.
    /// </summary>
    public AboutViewModel(INavigationService navigation)
    {
        _navigation = navigation;
        SourceCommand = Mvvm.BrowserLink.To(SourceUrl);
        BugsCommand = Mvvm.BrowserLink.To(IssuesUrl);
        PrivacyCommand = new Mvvm.AsyncRelayCommand((_, ct) => _navigation.GoAsync(AppShell.PrivacyRoute, ct));
    }

    /// <summary>
    /// Build version (Android versionName) per the microcopy.
    /// </summary>
    public string VersionLine { get; } = string.Format(AppStrings.AboutVersion, AppInfo.Current.VersionString);

    public Mvvm.AsyncRelayCommand SourceCommand { get; }

    public Mvvm.AsyncRelayCommand BugsCommand { get; }

    public Mvvm.AsyncRelayCommand PrivacyCommand { get; }
}
