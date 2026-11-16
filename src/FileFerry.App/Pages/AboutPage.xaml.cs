using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// About screen — a static screen; the code-behind is a DI adapter only (data and
/// actions live in the VM).
/// </summary>
public sealed partial class AboutPage : ContentPage
{
    public AboutPage(AboutViewModel viewModel)
    {
        BindingContext = viewModel;
        InitializeComponent();
    }
}
