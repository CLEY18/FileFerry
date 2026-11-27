using FileFerry.App.Navigation;
using FileFerry.App.Services;
using FileFerry.App.Theming;
using FileFerry.Core.Ports;

namespace FileFerry.App;

/// <summary>
/// Application root: Shell stack and startup gate — first launch with no account
/// at all lands on the welcome screen; everything else goes to the pair list.
/// </summary>
public sealed partial class App : Application
{
    public App() => InitializeComponent();

    /// <summary>
    /// Window with the Shell stack (screen map); the MainPage setter is
    /// obsolete.NET 10.
    /// </summary>
    protected override Window CreateWindow(IActivationState? activationState)
    {
        // Theme is applied before the first render: token resources are already merged from App.xaml.
        var theme = MauiProgram.Application.Services.GetRequiredService<IThemeService>();
        AndroidAppBar.Attach(theme); // the app bar lives outside the resource tree: it repaints on the event
        theme.Initialize();
        var shell = new AppShell();
        shell.Loaded += async (_, _) => await StartupGateAsync();
        // (toolbar and status strip repaint with the stored palette from the shell's own
        // Loaded/Navigated wiring - see AppShell)
        return new Window(shell);
    }

    /// <summary>
    /// First-launch gate: DB schema → account list → if empty, open the welcome screen. An unexpected
    /// DB failure is not swallowed: a silently "broken" start is worse than a visible
    /// one. The pass is shared with the pair-list gate — both race on launch, the screen opens once.
    /// </summary>
    private static async Task StartupGateAsync()
    {
        var services = MauiProgram.Application.Services;
        await services.GetRequiredService<DatabaseInitializer>().EnsureReadyAsync();
        // deleted pairs keep their buffer copies only until the TTL ends — the run purge cannot
        // see them (their pair is gone), so the moment the schema is ready is their collection day
        await services.GetRequiredService<Services.IOrphanedCopySweeper>().SweepAsync(CancellationToken.None);
        var accounts = await services.GetRequiredService<IAccountRepository>().ListAsync(CancellationToken.None);
        if (accounts.Count == 0 && services.GetRequiredService<Services.WelcomeGate>().TryOpen())
        {
            await services.GetRequiredService<INavigationService>().GoAsync(AppShell.WelcomeRoute, CancellationToken.None);
        }
    }
}
