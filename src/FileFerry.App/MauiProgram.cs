using FileFerry.App.Navigation;
using FileFerry.App.Pages;
using FileFerry.App.Services;
using FileFerry.App.Theming;
using FileFerry.App.ViewModels;
using FileFerry.Client.LoginFlow;
using FileFerry.Core.Ports;
using FileFerry.Core.Sync;
using FileFerry.Data.Sqlite;

namespace FileFerry.App;

/// <summary>
/// Application composition root: the MauiApp DI host assembling UI services,
/// view models, pages and storage adapters.
/// </summary>
public static class MauiProgram
{
    /// <summary>
    /// The built application — reachable from entry points outside the DI pipeline (App/CreateWindow).
    /// </summary>
    public static MauiApp Application { get; private set; } = null!;

    /// <summary>
    /// Assemble and build the <see cref="MauiApp"/>: Shell screens, navigation, theme and storages in DI.
    /// </summary>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>() // text font is the system Roboto — no EmbeddedFont for text
            .ConfigureFonts(fonts =>
                fonts.AddFont("MaterialSymbolsOutlined.ttf", GlyphMapping.FontFamily)); // icons 

#if ANDROID
        // tappable rows/chips draw the platform pressed highlight; the stock container never
        // enters the pressed state under the framework gesture pipeline, so the view gets its handler
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<Views.PressableBorder, PressableBorderHandler>());
#endif

        // UI services (the port is declared by its consumer — App).
        builder.Services.AddSingleton<INavigationService, ShellNavigationService>();
        builder.Services.AddSingleton<IThemePaletteSource, AndroidThemePalette>();
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<IAppDialogs, MauiAppDialogs>();
        builder.Services.AddSingleton<LoginSession>();
        builder.Services.AddSingleton<Services.WelcomeGate>();
        builder.Services.AddSingleton<RemoteStoreFactory>();
        builder.Services.AddSingleton<ILocalFolderPicker, AndroidSafFolderPicker>();
        builder.Services.AddSingleton<INotificationPermission, AndroidNotificationPermission>();
        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IHashProvider, Sha256HashProvider>(); // "same content" evidence
        builder.Services.AddSingleton(_ => new StashPolicyOptions()); // 30-day TTL — Options default
        builder.Services.AddSingleton<IStashPolicy>(sp => new StashPolicy(sp.GetRequiredService<StashPolicyOptions>()));
        builder.Services.AddSingleton(sp => new SyncPlanner(
            new MergePlanner(new KeepBothStrategy(), sp.GetRequiredService<IStashPolicy>()),
            sp.GetRequiredService<IStashPolicy>()));
        builder.Services.AddSingleton<ISyncScopeFactory, AndroidSyncScopeFactory>();
        builder.Services.AddSingleton<ISyncScheduler, AndroidWorkScheduler>();
        builder.Services.AddSingleton<Services.SyncStatusService>(); // Worker→UI hub (#3)
        // A factory, not auto-resolution: DI hands back an EMPTY (not null)
        // IEnumerable<IOperationHandler> when none is registered — the executor would have no handlers.
        builder.Services.AddSingleton(sp => new SyncRun(
            sp.GetRequiredService<ISyncScopeFactory>(),
            sp.GetRequiredService<SyncPlanner>(),
            sp.GetRequiredService<ISyncStateRepository>(),
            sp.GetRequiredService<IStashPolicy>(),
            sp.GetRequiredService<IClock>(),
            handlers: null,
            observer: sp.GetRequiredService<Services.SyncStatusService>(), // keyed per-pair run locks live in it (invariant)
            hasher: sp.GetRequiredService<IHashProvider>(), // hash for evidence
            stashIndex: sp.GetRequiredService<IStashRepository>())); // TTL-purges the stash buffer each run
        builder.Services.AddSingleton<IGeneralSettings, PreferencesGeneralSettings>();
        builder.Services.AddSingleton<IBatteryOptimization, AndroidBatteryOptimization>();

        // State/configuration adapters + secrets on the Keystore.
        builder.Services.AddSingleton(_ => new SqliteStore(Path.Combine(FileSystem.AppDataDirectory, "fileferry.db")));
        builder.Services.AddSingleton<DatabaseInitializer>();
        builder.Services.AddSingleton<IAccountRepository, SqliteAccountRepository>();
        builder.Services.AddSingleton<IFolderPairRepository, SqliteFolderPairRepository>();
        builder.Services.AddSingleton<ISyncStateRepository, SqliteSyncStateRepository>();
        builder.Services.AddSingleton<IStashRepository, SqliteStashRepository>();
        builder.Services.AddSingleton<ISecretStore, AndroidKeystoreSecretStore>();

        // Login Flow v2: one HttpClient per app — connection pooling is the correct behavior.
        builder.Services.AddSingleton<ILoginFlow>(_ => new LoginFlowV2Client(new HttpClient()));

        // Pages and VMs: transient — a screen lives exactly as long as it is in the Shell stack.
        builder.Services.AddTransient<PairListViewModel>();
        builder.Services.AddTransient<WelcomeViewModel>();
        builder.Services.AddTransient<LoginViewModel>();
        builder.Services.AddTransient<PairWizardViewModel>();
        builder.Services.AddTransient<PairDetailsViewModel>();
        builder.Services.AddTransient<ConflictsViewModel>();
        builder.Services.AddTransient<StashViewModel>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<LogViewModel>();
        builder.Services.AddTransient<AboutViewModel>();
        builder.Services.AddTransient<PrivacyViewModel>();
        builder.Services.AddTransient<PairListPage>();
        builder.Services.AddTransient<WelcomePage>();
        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<PairWizardPage>();
        builder.Services.AddTransient<PairDetailsPage>();
        builder.Services.AddTransient<ConflictsPage>();
        builder.Services.AddTransient<StashPage>();
        builder.Services.AddTransient<LogPage>();
        builder.Services.AddTransient<AboutPage>();
        builder.Services.AddTransient<PrivacyPage>();
        builder.Services.AddTransient<SettingsPage>();

        Application = builder.Build();
        return Application;
    }
}
