using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Login Flow v2 WebView screen: the page only mounts the VM; the sole
/// "code" is the one-time flow start on appear (no logic in code-behind).
/// </summary>
public sealed partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _viewModel;
    private bool _started;

    public LoginPage(LoginViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_started)
        {
            _started = true;
            _viewModel.Start();
        }
    }
}
