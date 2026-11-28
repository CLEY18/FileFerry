using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Trash buffer screen — a pair's deletion stash; the code-behind is a DI adapter, the <c>id</c>
/// query parameter and a reload on appear (everything lives in <see cref="StashViewModel"/>).
/// </summary>
public sealed partial class StashPage : ContentPage, IQueryAttributable
{
    private readonly StashViewModel _viewModel;
    private Guid? _pairId;

    public StashPage(StashViewModel viewModel)
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
        _viewModel.Detach(); // a restore dialog or notice must not outlive this screen
    }
}
