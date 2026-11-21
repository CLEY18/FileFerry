namespace FileFerry.App.ViewModels;

/// <summary>
/// VM for the privacy screen: a static summary, one link to the full policy
/// document published with the public repository.
/// </summary>
public sealed class PrivacyViewModel
{
    /// <summary>
    /// The policy document shipped at the root of the public repository.
    /// </summary>
    private const string PolicyUrl = "https://github.com/CLEY18/FileFerry/blob/main/PRIVACY.md";

    public Mvvm.AsyncRelayCommand FullPolicyCommand { get; } = Mvvm.BrowserLink.To(PolicyUrl);
}
