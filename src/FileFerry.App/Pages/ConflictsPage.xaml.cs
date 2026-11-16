using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Conflicts screen — a pair's conflict list; the code-behind is a DI adapter, the
/// <c>id</c> query parameter and a reload on appear (everything lives 
/// <see cref="ConflictsViewModel"/>).
/// </summary>
public sealed partial class ConflictsPage : ContentPage, IQueryAttributable
{
    private readonly ConflictsViewModel _viewModel;
    private Guid? _pairId;

    public ConflictsPage(ConflictsViewModel viewModel)
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
}
