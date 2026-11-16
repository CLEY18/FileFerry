using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Global log — global event log; the code-behind is a DI adapter plus a one-time load
/// on appear (no auto-scroll).
/// </summary>
public sealed partial class LogPage : ContentPage
{
    private readonly LogViewModel _viewModel;

    public LogPage(LogViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.LoadAsync(CancellationToken.None);
    }
}
