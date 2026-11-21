using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Privacy screen - a static screen; the code-behind is a DI adapter only (the one
/// action lives in the VM).
/// </summary>
public sealed partial class PrivacyPage : ContentPage
{
    public PrivacyPage(PrivacyViewModel viewModel)
    {
        BindingContext = viewModel;
        InitializeComponent();
    }
}
