using FileFerry.App.Pages;

namespace FileFerry.App;

/// <summary>
/// Application Shell stack: flyout items live in XAML; modal routes
/// are registered here.
/// </summary>
public sealed partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(WelcomeRoute, typeof(WelcomePage));
        Routing.RegisterRoute(LoginRoute, typeof(Pages.LoginPage));
        Routing.RegisterRoute(PairWizardRoute, typeof(Pages.PairWizardPage));
        Routing.RegisterRoute(PairDetailsRoute, typeof(Pages.PairDetailsPage));
        Routing.RegisterRoute(ConflictsRoute, typeof(Pages.ConflictsPage));
        Routing.RegisterRoute(StashRoute, typeof(Pages.StashPage));
        Routing.RegisterRoute(AboutRoute, typeof(Pages.AboutPage));
        Routing.RegisterRoute(PrivacyRoute, typeof(PrivacyPage));

        // The shell paints its own chrome: it is replaced wholesale on logout, and a repaint
        // wired to only the first instance leaves every later one with the framework's default
        // status strip and drawer edge. MAUI re-applies that default on every navigation, so
        // the token paint must land after each pass; Loaded paints the toolbar once it exists.
        Loaded += (_, _) => AndroidAppBar.Repaint();
        Navigated += (_, _) => AndroidAppBar.RepaintAfterNavigation();
    }

    /// <summary>
    /// Route of the welcome screen — first launch with no account yet.
    /// </summary>
    public const string WelcomeRoute = "welcome";

    /// <summary>
    /// Route of the Login Flow v2 WebView screen (modal over the welcome screen).
    /// </summary>
    public const string LoginRoute = "login";

    /// <summary>
    /// Route of the add-pair wizard — adding a pair (modal over the pair list).
    /// </summary>
    public const string PairWizardRoute = "pair/add";

    /// <summary>
    /// Route of the pair details — one pair's details (query <c>?id=&lt;guid&gt;</c>).
    /// </summary>
    public const string PairDetailsRoute = "pair/details";

    /// <summary>
    /// Route of the conflicts screen — a pair's conflicts (query <c>?id=&lt;guid&gt;</c>).
    /// </summary>
    public const string ConflictsRoute = "pair/conflicts";

    /// <summary>
    /// Route of the trash buffer screen — a pair's deletion stash (query <c>?id=&lt;guid&gt;</c>).
    /// </summary>
    public const string StashRoute = "pair/stash";

    /// <summary>
    /// Route of the about screen — logotype, version, disclaimers and links.
    /// </summary>
    public const string AboutRoute = "about";

    /// <summary>
    /// Route of the privacy screen — the in-app policy summary (opens from the about screen).
    /// </summary>
    public const string PrivacyRoute = "privacy";

    /// <summary>
    /// Absolute flyout route to the settings screen (the account chip leads here).
    /// </summary>
    public const string SettingsRoute = "//settings";

    /// <summary>
    /// Absolute route of the main pair list tab (a successful login resets the stack here).
    /// </summary>
    public const string PairListRoute = "//pairlist";
}
