using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Settings screen — the code-behind is a DI adapter plus a value reload on appear
/// (battery status/account may have changed off-screen; logic lives in the VM).
/// </summary>
public sealed partial class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.RefreshAsync(CancellationToken.None);
    }
}
