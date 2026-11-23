using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using NEREA.App.Services;
using NEREA.App.ViewModels;
using NEREA.Audio;
using NEREA.Core.Content;
using NEREA.Core.Engine.AudioAnalysis;
using NEREA.Core.Library;
using NEREA.Core.Persistence;
using NEREA.Core.Utilities;

namespace NEREA.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // A single bad frame / bad file must never take the whole player down.
            Log.Error("Unhandled exception.", e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppPaths.EnsureCreated();
        Services = BuildServices();
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static IServiceProvider BuildServices()
    {
        var services = new ServiceCollection();

        // Core / engine singletons
        services.AddSingleton(DispatcherQueue.GetForCurrentThread());
        services.AddSingleton(_ => ContentCatalog.Load());
        services.AddSingleton<SettingsService>();
        services.AddSingleton<AquariumService>();
        services.AddSingleton(_ =>
        {
            var db = new LibraryDatabase(AppPaths.LibraryDatabaseFile);
            try { db.Initialize(); }
            catch (Exception ex) { Log.Error("Library database could not be opened.", ex); }
            return db;
        });
        services.AddSingleton<LibraryScanner>();
        services.AddSingleton<SpectrumAnalyzer>();
        // Created on the UI thread so playback events are marshalled back to it.
        services.AddSingleton(sp => new AudioEngine(sp.GetRequiredService<SpectrumAnalyzer>()));
        services.AddSingleton<EcosystemService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<FilePickerService>();
        services.AddSingleton<SmtcService>();

        // View models
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<LibraryViewModel>();
        services.AddSingleton<WorldsViewModel>();
        services.AddSingleton<AquariumViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ShellViewModel>();

        return services.BuildServiceProvider();
    }
}
