namespace FileFerry.App.Mvvm;

/// <summary>
/// Command factory for external links: hands the address to the system browser.
/// </summary>
internal static class BrowserLink
{
    public static AsyncRelayCommand To(string url) => new(
        async (_, _) => await Browser.Default.OpenAsync(new Uri(url)));
}
