using FileFerry.App.ViewModels;

namespace FileFerry.App.Pages;

/// <summary>
/// Add-pair wizard — three steps on one route; the code-behind is a DI adapter and a
/// one-time loader on appear (all logic lives in <see cref="PairWizardViewModel"/>).
/// </summary>
public sealed partial class PairWizardPage : ContentPage
{
    private readonly PairWizardViewModel _viewModel;

    public PairWizardPage(PairWizardViewModel viewModel)
    {
        _viewModel = viewModel;
        BindingContext = viewModel;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAppearing()
    {
        base.OnAppearing();
        _ = _viewModel.LoadAsync(CancellationToken.None); // idempotent — repeat appearances are ignored
    }
}
