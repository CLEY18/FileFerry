using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Pair list — the main screen; the code-behind is a DI adapter and a reload
/// trigger on appear (all logic lives in <see cref="PairListViewModel"/>).
/// </summary>
public sealed partial class PairListPage : ContentPage
{
    private readonly PairListViewModel _viewModel;

    public PairListPage(PairListViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.RefreshAsync(); // quiet config reload + subscribe to run state
    }

    /// <inheritdoc />
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Detach(); // unsubscribe from the state hub
    }
}
