using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Pair details — details of one pair. Code-behind: DI adapter, the route query
/// parameter <c>id</c> and a reload trigger on appear; logic lives in the VM.
/// </summary>
public sealed partial class PairDetailsPage : ContentPage, IQueryAttributable
{
    private readonly PairDetailsViewModel _viewModel;
    private Guid? _pairId;

    public PairDetailsPage(PairDetailsViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    /// <inheritdoc />
    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        if (query.TryGetValue("id", out var raw) && Guid.TryParse(raw?.ToString(), out var id))
        {
            _pairId = id;
        }
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_pairId is { } id)
        {
            _ = _viewModel.LoadAsync(id, CancellationToken.None);
        }
    }

    /// <inheritdoc />
    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _viewModel.Detach(); // unsubscribe from the run-state hub
    }
}
