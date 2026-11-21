using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Login Flow v2 WebView screen: the page only mounts the VM; the code-behind starts the flow once
/// on appear and holds the secure-window mark for as long as the sign-in page is on screen.
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
        AndroidSecureScreen.SetSecure(true);
        if (!_started)
        {
            _started = true;
            _viewModel.Start();
        }
    }

    /// <inheritdoc />
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        AndroidSecureScreen.SetSecure(false);
    }
}
