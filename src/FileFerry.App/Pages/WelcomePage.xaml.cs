using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Welcome screen — first launch; the manual address field always stays (DNS may fail to resolve).
/// All behavior lives in <see cref="WelcomeViewModel"/>; the code-behind is a DI adapter only.
/// </summary>
public sealed partial class WelcomePage : ContentPage
{
    public WelcomePage(WelcomeViewModel viewModel)
    {
        BindingContext = viewModel;
        InitializeComponent();
    }
}
