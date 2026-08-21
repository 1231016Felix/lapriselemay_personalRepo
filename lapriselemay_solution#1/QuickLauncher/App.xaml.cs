using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using QuickLauncher.Models;
using QuickLauncher.Services;
using QuickLauncher.Views;
using QuickLauncher.Services.CommandHandlers;
using QuickLauncher.ViewModels;
using Velopack;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace QuickLauncher;

public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private HotkeyService? _hotkeyService;
    private LauncherWindow? _launcherWindow;
    private DispatcherTimer? _autoReindexTimer;
    private DateTime? _lastScheduledReindex;
    private bool _cleanedUp;
    private readonly ILogger _logger = new FileLogger(appName: Constants.AppName);

    /// <summary>
    /// Conteneur d'injection de dépendances.
    /// Centralise la création et la durée de vie des services.
    /// </summary>
    public static ServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Point d'entrée explicite (remplace le Main auto-généré par WPF).
    /// Velopack doit s'exécuter en tout premier : lors d'une installation ou d'une
    /// mise à jour, il intercepte le lancement pour créer les raccourcis puis
    /// termine le processus. Aucun code applicatif ne doit s'exécuter avant.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnFirstRun(_ => UpdateService.OnFirstRun())
            .Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Brancher le pont de log statique AVANT toute résolution DI :
        // AppSettings.Load / SecureStorageService s'exécutent dès la création
        // du SettingsProvider et doivent tracer dans le fichier de log.
        Log.Logger = _logger;

        SetupExceptionHandling();
        
        try
        {
            _logger.Info("=== Démarrage QuickLauncher ===");
            base.OnStartup(e);
            
            // === Configuration DI ===
            var serviceCollection = new ServiceCollection();
            ConfigureServices(serviceCollection);
            Services = serviceCollection.BuildServiceProvider();
            
            var settingsProvider = Services.GetRequiredService<ISettingsProvider>();
            var settings = settingsProvider.Current;
            
            _logger.Info("Initialisation du cache d'icônes persistant...");
            IconExtractorService.InitializePersistentCache();
            
            _logger.Info("Initialisation du thème...");
            var themeService = Services.GetRequiredService<ThemeService>();
            themeService.Initialize();
            themeService.ApplyTheme(settings.Appearance.Theme);
            themeService.ApplyAccentColor(settings.Appearance.AccentColor);
            
            _logger.Info("Synchronisation registre démarrage...");
            SettingsWindow.SyncStartupRegistry();
            
            var indexingService = Services.GetRequiredService<IndexingService>();
            
            _logger.Info("Démarrage indexation intelligente...");
            var fileWatcherService = Services.GetRequiredService<FileWatcherService>();
            _ = indexingService.SmartStartIndexingAsync().ContinueWith(_ =>
            {
                // Démarrer le FileWatcher après l'indexation, même si elle a échoué
                try { fileWatcherService.Start(); }
                catch (Exception ex) { _logger.Warning($"Erreur démarrage FileWatcher: {ex.Message}"); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
            
            _logger.Info("Restauration des widgets de notes et minuteries...");
            var noteWidgetService = Services.GetRequiredService<NoteWidgetService>();
            noteWidgetService.RestoreWidgets();
            
            var timerWidgetService = Services.GetRequiredService<TimerWidgetService>();
            timerWidgetService.RestoreWidgets();
            
            _logger.Info("Création icône système...");
            CreateTrayIcon(settings);
            
            _logger.Info("Enregistrement hotkey...");
            _hotkeyService = new HotkeyService(settings.Hotkey);
            _hotkeyService.HotkeyPressed += OnHotkeyPressed;
            
            if (!_hotkeyService.Register())
                _logger.Warning($"Impossible d'enregistrer le raccourci {settings.Hotkey.DisplayText}");
            
            _logger.Info("Configuration réindexation auto...");
            SetupAutoReindex();
            
            _logger.Info("Démarrage terminé!");
            
            // Construire la fenêtre du launcher dès que le dispatcher est libre,
            // pour que le premier appui sur le raccourci n'ait plus à la payer.
            _ = Dispatcher.InvokeAsync(PrewarmLauncherWindow, DispatcherPriority.ApplicationIdle);
            
            // Vérification des mises à jour en arrière-plan (non bloquante).
            // Volontairement après "Démarrage terminé" : un repo injoignable ou
            // un réseau lent ne doit jamais retarder l'affichage du launcher.
            _ = CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            _logger.Error("Erreur au démarrage", ex);
            MessageBox.Show($"Erreur au démarrage:\n{ex.Message}", Constants.AppName, 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Configure les services dans le conteneur DI.
    /// </summary>
    private void ConfigureServices(IServiceCollection services)
    {
        // Logger partagé
        services.AddSingleton<ILogger>(_logger);
        
        // Settings centralisés (cache en mémoire, événement SettingsChanged)
        services.AddSingleton<ISettingsProvider, SettingsProvider>();
        
        // Mises à jour automatiques via GitHub Releases (Velopack)
        services.AddSingleton<UpdateService>();
        
        // Services principaux
        services.AddSingleton<FolderFingerprintService>();
        services.AddSingleton<IndexingService>();
        services.AddSingleton<AliasService>();
        
        // FileWatcher (optionnel, enregistré mais peut échouer à l'init)
        services.AddSingleton<FileWatcherService>();
        
        // Services de widgets (migrés depuis singletons manuels)
        services.AddSingleton<NotesService>();
        services.AddSingleton<NoteWidgetService>();
        services.AddSingleton<TimerWidgetService>();
        
        // Recherche universelle (Everything / Windows Search / directe)
        services.AddSingleton<UniversalSearchService>();
        
        // Service de recherche (scoring, filtrage) — Amélioration #3
        services.AddSingleton<SearchService>();
        
        // Thème (gère Dark/Light/Auto/System) — Amélioration #4
        services.AddSingleton<ThemeService>();
        
        // Chargement d'icônes (Amélioration #1/#5)
        services.AddSingleton<IIconLoader, IconLoaderService>();
        
        // Suggestion fantôme (ghost text / autocomplétion)
        services.AddSingleton<GhostSuggestionService>();
        
        // Intégrations web (météo, traduction)
        services.AddSingleton<WebIntegrationService>();
        
        // Assistant IA
        services.AddSingleton<AiChatService>();
        
        // === Command Handlers (chaque handler gère un type de commande :xxx) ===
        services.AddSingleton<ICommandHandler, WeatherCommandHandler>();
        services.AddSingleton<ICommandHandler, TranslationCommandHandler>();
        services.AddSingleton<ICommandHandler, AiCommandHandler>();
        services.AddSingleton<ICommandHandler, WindowsSearchCommandHandler>();
        services.AddSingleton<CommandRouter>();
        
        // === System Control Executor (exécution des commandes système via Entrée) ===
        services.AddSingleton<ISystemControlExecutor, SystemControlExecutor>();
        
        // === Services autrefois statiques, migrés vers DI (Amélioration #2) ===
        services.AddSingleton<IStoreAppService, StoreAppService>();
        services.AddSingleton<IBookmarkService, BookmarkService>();
        services.AddSingleton<IWindowsSettingsProvider, WindowsSettingsProvider>();
        services.AddSingleton<ICalculatorService, CalculatorService>();
        services.AddSingleton<IShortcutHelper, ShortcutHelper>();
        services.AddSingleton<IFileActionExecutor, FileActionExecutor>();
        services.AddSingleton<IFileActionsService, FileActionsService>();
        
        // === Gestion épingles & actions (extraits du ViewModel — Points #1 et #2) ===
        services.AddSingleton<PinnedItemsManager>();
        services.AddSingleton<ResultActionService>();
        
        // === Suggestions commandes système (extrait du ViewModel — Point #5) ===
        services.AddSingleton<SystemControlSuggestionService>();
        
        // === Lancement et actions fichier (Point #6 : migration static → DI) ===
        services.AddSingleton<ILaunchService, LaunchService>();
        services.AddSingleton<IFileActionProvider, FileActionProvider>();
        
        // === Façades (regroupement des dépendances pour le ViewModel) ===
        services.AddSingleton<SearchFacade>();
        services.AddSingleton<ActionFacade>();
        
        // === ViewModel et fenêtre principale (singletons réutilisés entre Show/Hide) ===
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<LauncherWindow>();
        
        _logger.Info("Services DI configurés");
    }

    private void SetupExceptionHandling()
    {
        DispatcherUnhandledException += (_, ex) =>
        {
            _logger.Error("Erreur UI non gérée", ex.Exception);
            ex.Handled = true;
        };
        
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            _logger.Error("Erreur fatale", ex.ExceptionObject as Exception);
        };
        
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            _logger.Error("Erreur Task non observée", ex.Exception);
            ex.SetObserved();
        };
    }

    private void CreateTrayIcon(AppSettings settings)
    {
        try
        {
            _trayIcon = new TaskbarIcon
            {
                ToolTipText = $"{Constants.AppName} - {settings.Hotkey.DisplayText} pour ouvrir",
                Icon = GetAppIcon(),
                ContextMenu = CreateContextMenu(settings),
                Visibility = Visibility.Visible
            };
            
            _trayIcon.TrayMouseDoubleClick += (_, _) => ShowLauncher();
            _trayIcon.ForceCreate();
            
            _logger.Info("Icône système créée");
        }
        catch (Exception ex)
        {
            _logger.Error("Erreur création TrayIcon", ex);
        }
    }
    
    private static Icon GetAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute);
            var streamInfo = GetResourceStream(uri);
            if (streamInfo != null)
            {
                using var stream = streamInfo.Stream;
                return new Icon(stream);
            }
        }
        catch { /* Utilise l'icône par défaut */ }
        
        return SystemIcons.Application;
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu(AppSettings settings)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // Icônes : glyphes Segoe MDL2 Assets (même langage que la fenêtre principale)
        AddMenuItem(menu, "\uE721", $"Ouvrir ({settings.Hotkey.DisplayText})", ShowLauncher);
        menu.Items.Add(new System.Windows.Controls.Separator());
        AddMenuItem(menu, "\uE713", "Paramètres...", ShowSettings);
        AddMenuItem(menu, "\uE72C", "Réindexer", async () => await ReindexAsync());
        menu.Items.Add(new System.Windows.Controls.Separator());
        AddMenuItem(menu, "\uE896", "Vérifier les mises à jour", async () => await CheckForUpdatesAsync(silent: false));
        AddMenuItem(menu, "\uE897", "Aide", ShowHelp);
        menu.Items.Add(new System.Windows.Controls.Separator());
        AddMenuItem(menu, "\uE7E8", "Quitter", ExitApplication);

        return menu;
    }

    private static void AddMenuItem(System.Windows.Controls.ContextMenu menu, string glyph, string header, Action action)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = header,
            Icon = new System.Windows.Controls.TextBlock
            {
                Text = glyph,
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        _logger.Info("Hotkey pressé");
        Dispatcher.Invoke(ShowLauncher);
    }

    /// <summary>
    /// Crée la fenêtre du launcher au premier appel et branche ses événements.
    ///
    /// La fenêtre est un singleton DI réutilisé entre Show/Hide. OnClosing est
    /// overridé pour empêcher la fermeture (Cancel + Hide), donc l'instance
    /// reste toujours valide une fois créée.
    /// </summary>
    private LauncherWindow EnsureLauncherWindow()
    {
        if (_launcherWindow is null)
        {
            _launcherWindow = Services.GetRequiredService<LauncherWindow>();
            _launcherWindow.RequestOpenSettings += (_, _) => Dispatcher.Invoke(ShowSettings);
            _launcherWindow.RequestQuit += (_, _) => Dispatcher.Invoke(ExitApplication);
            _launcherWindow.RequestReindex += async (_, _) => await Dispatcher.InvokeAsync(async () => await ReindexAsync());
        }
        
        return _launcherWindow;
    }
    
    /// <summary>
    /// Construit la fenêtre du launcher à l'avance, pendant un temps mort du dispatcher.
    ///
    /// Sans ce préchargement, le tout premier appui sur le raccourci payait la
    /// résolution DI, le parsing BAML complet de LauncherWindow.xaml, la résolution
    /// des dictionnaires de ressources et l'instanciation des convertisseurs —
    /// d'où une première ouverture nettement plus lente que les suivantes.
    ///
    /// On ne force volontairement PAS la création du HWND via Show()/Hide() :
    /// l'application démarrant avec Windows, un Show() même totalement transparent
    /// prendrait le focus pendant l'ouverture de session. Une passe de mesure
    /// couvre l'essentiel du coût sans jamais toucher au premier plan.
    /// </summary>
    private void PrewarmLauncherWindow()
    {
        try
        {
            var window = EnsureLauncherWindow();
            window.Measure(new System.Windows.Size(window.Width, double.PositiveInfinity));
            _logger.Info("Fenêtre du launcher préchargée");
        }
        catch (Exception ex)
        {
            // Best-effort : un échec ici ne coûte qu'une première ouverture plus lente.
            _logger.Warning($"Préchargement de la fenêtre ignoré: {ex.Message}");
        }
    }

    public void ShowLauncher()
    {
        try
        {
            var window = EnsureLauncherWindow();
            
            // L'ordre compte : tout le setup (settings, contenu, position, état de
            // départ de l'animation) doit être fait AVANT Show(), sinon la fenêtre
            // est présentée dans son état précédent puis remise à zéro par
            // l'animation — elle apparaît, disparaît, et s'anime seulement après.
            window.PrepareForShow();
            window.Show();
            window.Activate();
            window.BeginShowAnimation();
        }
        catch (Exception ex)
        {
            _logger.Error("Erreur ShowLauncher", ex);
        }
    }

    private void ShowSettings()
    {
        try
        {
            var indexingService = Services.GetRequiredService<IndexingService>();
            var settingsProvider = Services.GetRequiredService<ISettingsProvider>();
            var themeService = Services.GetRequiredService<ThemeService>();
            var universalSearchService = Services.GetRequiredService<UniversalSearchService>();
            
            var bookmarkService = Services.GetRequiredService<IBookmarkService>();
            var settingsWindow = new SettingsWindow(indexingService, settingsProvider, themeService, universalSearchService, bookmarkService);
            settingsWindow.ShowDialog();
            
            // Les paramètres sont déjà sauvegardés via le provider, mais on force un reload
            // pour être sûr de capter les modifications externes
            settingsProvider.Reload();
            var settings = settingsProvider.Current;
            
            // Réappliquer le thème et la couleur d'accent
            themeService.ApplyTheme(settings.Appearance.Theme);
            themeService.ApplyAccentColor(settings.Appearance.AccentColor);
            
            if (_trayIcon != null)
                _trayIcon.ToolTipText = $"{Constants.AppName} - {settings.Hotkey.DisplayText} pour ouvrir";
        }
        catch (Exception ex)
        {
            _logger.Error("Erreur Settings", ex);
        }
    }
    
    public void SetupAutoReindex()
    {
        _autoReindexTimer?.Stop();
        var settingsProvider = Services.GetRequiredService<ISettingsProvider>();
        var settings = settingsProvider.Current;
        
        if (!settings.Search.AutoReindexEnabled)
        {
            _logger.Info("Réindexation auto désactivée");
            return;
        }
        
        _autoReindexTimer = new DispatcherTimer();
        
        if (settings.Search.AutoReindexMode == AutoReindexMode.Interval)
        {
            _autoReindexTimer.Interval = TimeSpan.FromMinutes(settings.Search.AutoReindexIntervalMinutes);
            _autoReindexTimer.Tick += async (_, _) =>
            {
                _logger.Info($"Réindexation auto (intervalle {settings.Search.AutoReindexIntervalMinutes} min)");
                await ReindexAsync();
            };
            
            _logger.Info($"Timer réindexation: toutes les {settings.Search.AutoReindexIntervalMinutes} minutes");
        }
        else
        {
            _autoReindexTimer.Interval = TimeSpan.FromMinutes(1);
            _autoReindexTimer.Tick += async (_, _) =>
            {
                var now = DateTime.Now;
                var parts = settings.Search.AutoReindexScheduledTime.Split(':');
                
                if (parts.Length == 2 &&
                    int.TryParse(parts[0], out var hour) &&
                    int.TryParse(parts[1], out var minute) &&
                    now.Hour == hour && now.Minute == minute &&
                    _lastScheduledReindex?.Date != now.Date)
                {
                    _lastScheduledReindex = now;
                    _logger.Info($"Réindexation auto (programmée {settings.Search.AutoReindexScheduledTime})");
                    await ReindexAsync();
                }
            };
            
            _logger.Info($"Timer réindexation: programmé à {settings.Search.AutoReindexScheduledTime}");
        }
        
        _autoReindexTimer.Start();
    }

    /// <summary>
    /// Vérifie et télécharge une éventuelle mise à jour.
    /// En mode <paramref name="silent"/> (démarrage), rien n'est affiché si l'app
    /// est à jour ou si la vérification échoue. En mode manuel (menu du tray),
    /// l'utilisateur reçoit toujours un retour.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        var updateService = Services.GetRequiredService<UpdateService>();
        
        if (!updateService.IsInstalled)
        {
            if (!silent)
            {
                MessageBox.Show(
                    "Les mises à jour automatiques ne sont disponibles que pour la version installée.\n\n" +
                    "Téléchargez l'installateur depuis les Releases GitHub du projet.",
                    $"{Constants.AppName} - Mises à jour",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        
        var newVersion = await updateService.CheckAndDownloadAsync();
        
        if (newVersion is null)
        {
            if (!silent)
            {
                MessageBox.Show(
                    $"{Constants.AppName} est à jour (version {updateService.CurrentVersion}).",
                    $"{Constants.AppName} - Mises à jour",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        
        var result = MessageBox.Show(
            $"La version {newVersion} a été téléchargée.\n\n" +
            "Redémarrer maintenant pour l'installer ?",
            $"{Constants.AppName} - Mise à jour disponible",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            Cleanup();
            updateService.ApplyPendingUpdateAndRestart();
        }
    }

    private async Task ReindexAsync()
    {
        try
        {
            var indexingService = Services.GetRequiredService<IndexingService>();
            await indexingService.ReindexAsync();
            _logger.Info("Réindexation terminée");
        }
        catch (Exception ex)
        {
            _logger.Error("Erreur Reindex", ex);
        }
    }
    
    private void ShowHelp()
    {
        var settingsProvider = Services.GetRequiredService<ISettingsProvider>();
        var settings = settingsProvider.Current;
        
        var helpText = $"""
            🚀 {Constants.AppName} - Aide

            📌 Raccourcis clavier:
            • {settings.Hotkey.DisplayText} - Ouvrir/Fermer {Constants.AppName}
            • Ctrl+, - Ouvrir les paramètres
            • Ctrl+R - Réindexer
            • Ctrl+Q - Quitter
            • Échap - Fermer la fenêtre

            📌 Commandes spéciales:
            • :settings - Ouvrir les paramètres
            • :reload - Réindexer les fichiers
            • :history - Voir l'historique
            • :clear - Effacer l'historique
            • :help ou ? - Afficher l'aide
            • :quit - Quitter l'application

            📌 Recherche web (préfixes):
            • g [texte] - Recherche Google
            • yt [texte] - Recherche YouTube
            • gh [texte] - Recherche GitHub
            • so [texte] - Recherche Stack Overflow
            """;
        
        MessageBox.Show(helpText, $"{Constants.AppName} - Aide", 
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExitApplication()
    {
        _logger.Info("Fermeture application...");
        Cleanup();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger.Info("OnExit");
        Cleanup();
        base.OnExit(e);
    }
    
    private void Cleanup()
    {
        // Cleanup est appelé deux fois sur le chemin normal de fermeture
        // (ExitApplication → Cleanup, puis Shutdown → OnExit → Cleanup).
        // Le second passage appellerait GetService sur un ServiceProvider
        // déjà disposé → ObjectDisposedException en plein shutdown.
        if (_cleanedUp) return;
        _cleanedUp = true;

        _autoReindexTimer?.Stop();
        _hotkeyService?.Unregister();
        _hotkeyService?.Dispose();
        
        // Fermer les widgets avant le dispose DI (ils ont des fenêtres WPF à fermer)
        if (Services != null)
        {
            Services.GetService<NoteWidgetService>()?.CloseAll();
            Services.GetService<TimerWidgetService>()?.CloseAll();
            
            // ServiceProvider.Dispose() appelle automatiquement Dispose()
            // sur tous les singletons IDisposable (IndexingService, FileWatcherService,
            // AliasService, etc.) — pas besoin de les disposer individuellement.
            Services.Dispose();
        }
        
        _trayIcon?.Dispose();
        DesktopAttachHelper.Shutdown();
        // ThemeService.Shutdown() est appelé automatiquement via Dispose() par le conteneur DI
        
        // Le FileLogger est créé avant le conteneur DI et enregistré comme instance externe.
        // Services.Dispose() ne dispose PAS les instances externes — il faut le faire manuellement.
        (_logger as IDisposable)?.Dispose();
    }
}
