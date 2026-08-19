using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using QuickLauncher.Models;
using QuickLauncher.Services;
using QuickLauncher.ViewModels;

using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace QuickLauncher.Views;

public partial class LauncherWindow : Window
{
    private readonly LauncherViewModel _viewModel;
    private readonly ISettingsProvider _settingsProvider;
    private readonly IFileActionProvider _fileActionProvider;
    private readonly IFileActionsService _fileActionsService;
    private readonly WindowAnimationHelper _animator;
    
    // === Drag & Drop state ===
    private System.Windows.Point _dragStartPoint;
    private bool _isDragging;
    private int _dragFromIndex = -1;
    private Border? _dropIndicator;
    
    // Flag pour empêcher le HideWindow pendant l'affichage d'un dialogue modal
    private bool _isDialogOpen;
    
    /// <summary>
    /// Accès rapide aux paramètres actuels (toujours à jour via ISettingsProvider).
    /// </summary>
    private AppSettings _settings => _settingsProvider.Current;
    
    public event EventHandler? RequestOpenSettings;
    public event EventHandler? RequestQuit;
    public event EventHandler? RequestReindex;
    
    public LauncherWindow(LauncherViewModel viewModel, ISettingsProvider settingsProvider,
        IFileActionProvider fileActionProvider, IFileActionsService fileActionsService)
    {
        InitializeComponent();
        
        _settingsProvider = settingsProvider;
        _fileActionProvider = fileActionProvider;
        _fileActionsService = fileActionsService;
        _viewModel = viewModel;
        DataContext = _viewModel;
        
        _animator = new WindowAnimationHelper(
            MainBorder, ShadowBorder, MainBorderTranslate, MainBorderScale,
            () => _settings);
        
        SetupEventHandlers();
        ApplySettings();
    }
    
    private void SetupEventHandlers()
    {
        _viewModel.RequestHide += (_, _) => HideWindow();
        _viewModel.RequestOpenSettings += (_, _) =>
        {
            // Le ViewModel appelle RequestHide juste avant → forcer immédiat pour éviter
            // que le dialog modal bloque le thread pendant que l'animation tourne
            HideWindowImmediate();
            RequestOpenSettings?.Invoke(this, EventArgs.Empty);
        };
        _viewModel.RequestQuit += (_, _) => RequestQuit?.Invoke(this, EventArgs.Empty);
        _viewModel.RequestReindex += (_, _) =>
        {
            HideWindowImmediate();
            RequestReindex?.Invoke(this, EventArgs.Empty);
        };
        _viewModel.RequestRename += OnRequestRename;
        _viewModel.ShowNotification += OnShowNotification;
        _viewModel.RequestCaretAtEnd += (_, _) => Dispatcher.BeginInvoke(() => SearchBox.CaretIndex = SearchBox.Text.Length);
        _viewModel.RequestScreenCapture += OnRequestScreenCapture;
        _viewModel.RequestCreateAlias += OnRequestCreateAlias;
        _viewModel.RequestDeleteConfirmation += OnRequestDeleteConfirmation;
        
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_viewModel.SearchText))
            {
                ClearButton.Visibility = string.IsNullOrEmpty(_viewModel.SearchText) 
                    ? Visibility.Collapsed 
                    : Visibility.Visible;
                
                // Activer le mode multiligne quand on écrit une note
                var noteCmd = _settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.Note);
                var notePrefix = noteCmd != null ? $":{noteCmd.Prefix} " : ":note ";
                var isNoteMode = _viewModel.SearchText.StartsWith(notePrefix, StringComparison.OrdinalIgnoreCase);
                SearchBox.TextWrapping = isNoteMode ? TextWrapping.Wrap : TextWrapping.NoWrap;
                SearchBox.AcceptsReturn = false; // Géré manuellement via Shift+Enter
            }
        };
        
        // Incrémenter la génération de recherche quand la liste est nettoyée
        // pour invalider les animations stagger du batch précédent
        _viewModel.Results.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
                _animator.IncrementSearchGeneration();
        };
    }
    
    private void OnRequestRename(object? sender, string path)
    {
        _isDialogOpen = true;
        try
        {
            var name = System.IO.Path.GetFileName(path);
            var dialog = new RenameDialog(name) { Owner = this };
            
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.NewName))
            {
                var success = _fileActionsService.Rename(path, dialog.NewName);
                if (success)
                {
                    RequestReindex?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    MessageBox.Show("Impossible de renommer le fichier.", "Erreur", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        finally
        {
            _isDialogOpen = false;
        }
    }
    
    private void OnRequestCreateAlias(object? sender, (string Name, string Path) args)
    {
        _isDialogOpen = true;
        try
        {
            var dialog = new AliasDialog(args.Name, args.Path) { Owner = this };
            
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.Alias))
            {
                _viewModel.SaveAlias(dialog.Alias, args.Path);
            }
        }
        finally
        {
            _isDialogOpen = false;
        }
    }
    
    private void OnRequestDeleteConfirmation(object? sender, SearchResult result)
    {
        ConfirmAndDelete(result);
    }
    
    /// <summary>
    /// Storyboard du toast en cours (pour pouvoir l'annuler si un nouveau toast arrive).
    /// </summary>
    private Storyboard? _toastStoryboard;
    
    /// <summary>
    /// Affiche un toast léger en bas de la fenêtre pendant 2 secondes.
    /// Si un toast est déjà visible, il est remplacé immédiatement.
    /// Point #10 : toast notification.
    /// </summary>
    private void OnShowNotification(object? sender, string message)
    {
        System.Diagnostics.Debug.WriteLine($"[Notification] {message}");
        
        // Annuler le toast précédent s'il est encore visible
        _toastStoryboard?.Stop(ToastBorder);
        
        ToastText.Text = message;
        
        // Animation : fade-in rapide → maintien → fade-out
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        var hold = new DoubleAnimation(1, 1, TimeSpan.FromSeconds(1.8)) { BeginTime = TimeSpan.FromMilliseconds(150) };
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromMilliseconds(1950) };
        
        var sb = new Storyboard();
        Storyboard.SetTarget(fadeIn, ToastBorder);
        Storyboard.SetTargetProperty(fadeIn, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(hold, ToastBorder);
        Storyboard.SetTargetProperty(hold, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(fadeOut, ToastBorder);
        Storyboard.SetTargetProperty(fadeOut, new PropertyPath(OpacityProperty));
        
        sb.Children.Add(fadeIn);
        sb.Children.Add(hold);
        sb.Children.Add(fadeOut);
        
        _toastStoryboard = sb;
        sb.Begin(ToastBorder, true);
    }

    private void OnRequestScreenCapture(object? sender, string? mode)
    {
        try
        {
            // Masquer immédiatement (sans animation) pour ne pas apparaître sur la capture
            HideWindowImmediate();
            
            var captureMode = mode?.ToLowerInvariant();
            
            if (captureMode is "snip" or "region" or "select")
            {
                // Capture de région avec overlay → ouvre l'annotateur
                var overlay = new ScreenshotOverlayWindow();
                if (overlay.ShowDialog() == true && overlay.CapturedRegion != null)
                {
                    var annotationWindow = new AnnotationWindow(overlay.CapturedRegion);
                    annotationWindow.ShowDialog();

                    if (!string.IsNullOrEmpty(annotationWindow.SavedFilePath))
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{annotationWindow.SavedFilePath}\"");
                }
            }
            else
            {
                // Capture plein écran ou écran principal → sauvegarde directe, sans rien afficher
                var primaryOnly = captureMode is "primary" or "main";
                var bitmap = CaptureScreen(primaryOnly);
                
                if (bitmap != null)
                {
                    var filePath = SaveScreenshotDirect(bitmap);
                    bitmap.Dispose();
                    
                    if (filePath != null)
                    {
                        // Copier aussi dans le presse-papier
                        var bitmapSource = LoadBitmapSourceFromFile(filePath);
                        if (bitmapSource != null)
                            System.Windows.Clipboard.SetImage(bitmapSource);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenCapture ERROR] {ex}");
            MessageBox.Show($"Erreur lors de la capture :\n{ex.GetType().Name}: {ex.Message}\n\nStack:\n{ex.StackTrace}", "Erreur",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Sauvegarde directe d'un bitmap sans ouvrir l'annotateur.
    /// </summary>
    private static string? SaveScreenshotDirect(System.Drawing.Bitmap bitmap)
    {
        try
        {
            var screenshotsFolder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "Screenshots");
            System.IO.Directory.CreateDirectory(screenshotsFolder);

            var fileName = $"Screenshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var filePath = System.IO.Path.Combine(screenshotsFolder, fileName);

            bitmap.Save(filePath, System.Drawing.Imaging.ImageFormat.Png);
            return filePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SaveScreenshotDirect ERROR] {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Charge un BitmapSource depuis un fichier PNG pour le presse-papier.
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapSource? LoadBitmapSourceFromFile(string filePath)
    {
        try
        {
            var bi = new System.Windows.Media.Imaging.BitmapImage();
            bi.BeginInit();
            bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(filePath);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch
        {
            return null;
        }
    }

    private static System.Drawing.Bitmap? CaptureScreen(bool primaryOnly)
    {
        try
        {
            var bounds = primaryOnly
                ? System.Windows.Forms.Screen.PrimaryScreen!.Bounds
                : System.Windows.Forms.Screen.AllScreens
                    .Select(s => s.Bounds)
                    .Aggregate(System.Drawing.Rectangle.Union);

            var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
    
    private void ApplySettings()
    {
        Opacity = _settings.Appearance.WindowOpacity;
        SettingsButton.Visibility = _settings.Appearance.ShowSettingsButton ? Visibility.Visible : Visibility.Collapsed;
    }
    
    /// <summary>
    /// Empêche la fermeture de la fenêtre (Alt+F4, système, etc.).
    /// La fenêtre est un singleton DI réutilisé entre Show/Hide.
    /// Une fermeture rendrait l'instance DI invalide (WPF interdit Show() après Close).
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Ne jamais fermer — seulement masquer.
        // La vraie fermeture se fait via App.ExitApplication() → Shutdown().
        e.Cancel = true;
        HideWindow();
    }
    
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        
        if (e.OriginalSource is System.Windows.Controls.TextBox or System.Windows.Controls.ListBoxItem)
            return;
            
        DragMove();
    }
    
    public void FocusSearchBox()
    {
        // Reload synchrone — le ViewModel a besoin des settings à jour pour Reset()
        _settingsProvider.Reload();
        ApplySettings();
        _viewModel.ReloadSettings();
        _viewModel.Reset();
        CenterOnScreen();
        
        // L'animation démarre après le setup complet
        _animator.PlayShowAnimation();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
    
    #region Animations (délégation au WindowAnimationHelper)
    
    private void PlayHideAnimation()
    {
        // Sauvegarder la position maintenant (avant le Hide)
        SaveWindowPosition();
        
        _animator.PlayHideAnimation(() =>
        {
            _viewModel.Reset();
            Hide();
        });
    }
    
    #endregion
    
    /// <summary>
    /// Animation d'apparition staggerée pour chaque item de la liste de résultats.
    /// Délégué au WindowAnimationHelper.
    /// </summary>
    private void ResultItem_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ListBoxItem item) return;
        var index = ResultsList.ItemContainerGenerator.IndexFromContainer(item);
        _animator.AnimateResultItem(item, index, ResultsList);
    }
    
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Shift+Enter : insérer un saut de ligne dans les notes
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift)
        {
            var noteCmd = _settings.SystemCommands.FirstOrDefault(c => c.Type == SystemControlType.Note);
            var notePrefix = noteCmd != null ? $":{noteCmd.Prefix} " : ":note ";
            
            if (_viewModel.SearchText.StartsWith(notePrefix, StringComparison.OrdinalIgnoreCase))
            {
                var caretIndex = SearchBox.CaretIndex;
                var text = SearchBox.Text;
                SearchBox.Text = text.Insert(caretIndex, "\n");
                SearchBox.CaretIndex = caretIndex + 1;
                e.Handled = true;
                return;
            }
        }
        
        // Raccourcis Alt+1 à Alt+9 pour lancer un résultat par index
        // En WPF, quand Alt est enfoncé, e.Key == Key.System et la vraie touche est dans e.SystemKey
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.System)
        {
            var index = e.SystemKey switch
            {
                Key.D1 => 0,
                Key.D2 => 1,
                Key.D3 => 2,
                Key.D4 => 3,
                Key.D5 => 4,
                Key.D6 => 5,
                Key.D7 => 6,
                Key.D8 => 7,
                Key.D9 => 8,
                Key.NumPad1 => 0,
                Key.NumPad2 => 1,
                Key.NumPad3 => 2,
                Key.NumPad4 => 3,
                Key.NumPad5 => 4,
                Key.NumPad6 => 5,
                Key.NumPad7 => 6,
                Key.NumPad8 => 7,
                Key.NumPad9 => 8,
                _ => -1
            };
            
            if (index >= 0 && index < _viewModel.Results.Count)
            {
                _viewModel.SelectedIndex = index;
                _viewModel.ExecuteCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        
        // Raccourcis avec Ctrl
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.OemComma:
                    HideWindowImmediate();
                    RequestOpenSettings?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                    
                case Key.R:
                    HideWindowImmediate();
                    RequestReindex?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                    
                case Key.Q:
                    RequestQuit?.Invoke(this, EventArgs.Empty);
                    e.Handled = true;
                    return;
                    
                case Key.T:
                    // Ouvrir terminal (délégué au ViewModel → ResultActionService)
                    _viewModel.ExecuteShortcutAction(FileActionType.OpenInTerminal);
                    e.Handled = true;
                    return;
            }
        }
        
        // Ctrl+Enter: Exécuter en admin
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Enter)
        {
            _viewModel.ExecuteAsAdminCommand.Execute(null);
            e.Handled = true;
            return;
        }
        
        // Ctrl+Shift+Enter: Ouvrir en navigation privée (délégué au ViewModel → ResultActionService)
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Enter)
        {
            _viewModel.ExecuteShortcutAction(FileActionType.OpenPrivate);
            e.Handled = true;
            return;
        }
        
        // Raccourcis sans modificateurs
        switch (e.Key)
        {
            case Key.Tab:
                // Priorité 1 : accepter la suggestion fantôme (ghost suggestion)
                if (_viewModel.AcceptGhostSuggestion())
                {
                    e.Handled = true;
                }
                // Priorité 2 : ouvrir le menu contextuel sur l'item sélectionné
                else if (_viewModel.SelectedIndex >= 0 && _viewModel.SelectedIndex < _viewModel.Results.Count)
                {
                    OpenContextMenuOnSelectedItem();
                    e.Handled = true;
                }
                break;
            
            case Key.Right:
                // Accepter la suggestion fantôme avec flèche droite (quand le caret est en fin de texte)
                if (SearchBox.CaretIndex == SearchBox.Text.Length 
                    && !string.IsNullOrEmpty(_viewModel.GhostSuggestionText))
                {
                    _viewModel.AcceptGhostSuggestion();
                    e.Handled = true;
                }
                break;
                
            case Key.Escape:
                HideWindow();
                e.Handled = true;
                break;
                
            case Key.Enter:
                _viewModel.ExecuteCommand.Execute(null);
                e.Handled = true;
                break;
                
            case Key.Down:
                _viewModel.MoveSelection(1);
                ScrollSelectedItemIntoView();
                e.Handled = true;
                break;
                
            case Key.Up:
                _viewModel.MoveSelection(-1);
                ScrollSelectedItemIntoView();
                e.Handled = true;
                break;
                
            case Key.F2:
                // Renommer (délégué au ViewModel → ResultActionService)
                _viewModel.ExecuteShortcutAction(FileActionType.Rename);
                e.Handled = true;
                break;
                
            case Key.Delete:
                // Supprimer (délégué au ViewModel → ResultActionService)
                _viewModel.ExecuteShortcutAction(FileActionType.Delete);
                e.Handled = true;
                break;
        }
    }
    
    private void ConfirmAndDelete(SearchResult item)
    {
        _isDialogOpen = true;
        try
        {
            var result = MessageBox.Show(
                $"Voulez-vous vraiment supprimer '{item.Name}' ?\n\nLe fichier sera envoyé à la corbeille.",
                "Confirmer la suppression",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (result == MessageBoxResult.Yes)
            {
                var success = _fileActionsService.DeleteToRecycleBin(item.Path);
                if (success)
                {
                    RequestReindex?.Invoke(this, EventArgs.Empty);
                    HideWindow();
                }
                else
                {
                    MessageBox.Show("Impossible de supprimer le fichier.", "Erreur", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        finally
        {
            _isDialogOpen = false;
        }
    }
    
    private void Window_Deactivated(object sender, EventArgs e)
    {
        // Pendant un hide animé ou un dialogue modal, on ignore
        if (_animator.IsAnimatingHide || _isDialogOpen) return;
        
        // Ne pas masquer quand le menu contextuel est ouvert
        // (AllowsTransparency + WindowStyle=None fait que le popup du ContextMenu
        // déclenche Deactivated sur la fenêtre parente)
        if (FindResource("ResultContextMenu") is ContextMenu cm && cm.IsOpen) return;
        
        HideWindow();
    }
    
    private void HideWindow()
    {
        if (!IsVisible || _animator.IsAnimatingHide) return;
        PlayHideAnimation();
    }
    
    /// <summary>
    /// Masque la fenêtre immédiatement sans animation.
    /// Utilisé quand on ouvre un dialog modal (Settings) ou quand on doit être synchrone.
    /// </summary>
    private void HideWindowImmediate()
    {
        SaveWindowPosition();
        _animator.HideImmediate();
        _viewModel.Reset();
        Hide();
    }
    
    private void SaveWindowPosition()
    {
        _settingsProvider.Update(s =>
        {
            s.Appearance.LastWindowLeft = Left;
            s.Appearance.LastWindowTop = Top;
        });
    }
    
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        HideWindowImmediate();
        RequestOpenSettings?.Invoke(this, EventArgs.Empty);
    }
    
    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SearchText = string.Empty;
        SearchBox.Focus();
    }
    
    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_settings.SingleClickLaunch)
        {
            // Les commandes système (CTRL/SYS) sont déjà exécutées au clic simple,
            // ne pas réexécuter au double-clic
            if (ResultsList.SelectedItem is SearchResult result 
                && result.Type is ResultType.SystemControl or ResultType.AppControl or ResultType.SystemCommand)
                return;
            
            _viewModel.ExecuteCommand.Execute(null);
        }
    }
    
    private void ResultsList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Ne pas lancer si on revient d'un drag & drop
        if (_isDragging)
            return;
        
        if (ResultsList.SelectedItem == null)
            return;
        
        // Vérifier qu'on a bien cliqué sur un item (pas sur le scrollbar)
        var container = ItemsControl.ContainerFromElement(ResultsList, (DependencyObject)e.OriginalSource) as ListBoxItem;
        if (container == null)
            return;
        
        // Les commandes système (CTRL/SYS) s'exécutent toujours en clic simple
        // car ce sont des actions, pas des fichiers à ouvrir
        if (ResultsList.SelectedItem is SearchResult result 
            && result.Type is ResultType.SystemControl or ResultType.AppControl or ResultType.SystemCommand)
        {
            _viewModel.ExecuteCommand.Execute(null);
            return;
        }
        
        // Lancement en clic simple pour les autres types si activé dans les paramètres
        if (_settings.SingleClickLaunch)
        {
            _viewModel.ExecuteCommand.Execute(null);
        }
    }
    
    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Forcer le rendu hardware pour cette fenêtre (contourne le software rendering d'AllowsTransparency)
        var hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        if (hwndSource?.CompositionTarget != null)
            hwndSource.CompositionTarget.RenderMode = RenderMode.Default;
        
        CenterOnScreen();
    }
    
    /// <summary>
    /// Fait défiler la liste pour rendre l'item sélectionné visible.
    /// Appelé après MoveSelection pour que les flèches Haut/Bas
    /// scrollent automatiquement la liste.
    /// </summary>
    private void ScrollSelectedItemIntoView()
    {
        if (_viewModel.SelectedIndex >= 0 && _viewModel.SelectedIndex < _viewModel.Results.Count)
        {
            ResultsList.ScrollIntoView(_viewModel.Results[_viewModel.SelectedIndex]);
        }
    }
    
    private void CenterOnScreen()
    {
        var workArea = SystemParameters.WorkArea; // Zone sans la barre des tâches
        var windowWidth = Width;
        var windowHeight = ActualHeight > 0 ? ActualHeight : 150;
        // Marge de sécurité en bas pour ne jamais coller à la barre des tâches
        const double bottomMargin = 10;
        
        if (_settings.Appearance.LastWindowLeft.HasValue && _settings.Appearance.LastWindowTop.HasValue)
        {
            var left = _settings.Appearance.LastWindowLeft.Value;
            var top = _settings.Appearance.LastWindowTop.Value;
            
            if (left >= workArea.Left && left + windowWidth <= workArea.Right &&
                top >= workArea.Top && top + windowHeight <= workArea.Bottom)
            {
                Left = left;
                Top = top;
            }
            else
            {
                // Position sauvegardée hors écran (changement de moniteur, etc.) → centrer
                Left = workArea.Left + (workArea.Width - windowWidth) / 2;
                Top = workArea.Top + workArea.Height / 4;
            }
        }
        else
        {
            // Première ouverture, aucune position sauvegardée → centrer
            Left = workArea.Left + (workArea.Width - windowWidth) / 2;
            Top = workArea.Top + workArea.Height / 4;
        }
        
        // Limiter MaxHeight dynamiquement pour ne jamais dépasser la zone de travail.
        // La fenêtre utilise SizeToContent=Height, donc WPF la redimensionne automatiquement
        // en respectant MaxHeight. Le Grid.Margin="20" ajoute 40px (ombre).
        var availableHeight = workArea.Bottom - Top - bottomMargin;
        MaxHeight = Math.Max(200, availableHeight);
    }

    #region Drag & Drop (réordonnement des épingles)

    private void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(ResultsList);
        _isDragging = false;
    }

    private void ResultsList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging)
            return;

        // Vérifier qu'on est dans la vue épingles (pas de recherche active)
        if (!string.IsNullOrWhiteSpace(_viewModel.SearchText))
            return;

        var pos = e.GetPosition(ResultsList);
        var diff = pos - _dragStartPoint;

        // Seuil de déplacement minimum pour éviter les faux drags
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // Trouver l'item sous le curseur au point de départ
        var item = GetListBoxItemAtPoint(_dragStartPoint);
        if (item == null) return;

        var index = ResultsList.ItemContainerGenerator.IndexFromContainer(item);
        if (index < 0 || !_viewModel.IsResultPinned(index))
            return;

        // Démarrer le drag
        _isDragging = true;
        _dragFromIndex = index;
        item.Opacity = 0.4;

        var data = new System.Windows.DataObject("PinnedItemIndex", index);
        System.Windows.DragDrop.DoDragDrop(ResultsList, data, System.Windows.DragDropEffects.Move);

        // Nettoyage après le drop (ou annulation)
        item.Opacity = 1;
        _isDragging = false;
        _dragFromIndex = -1;
        HideDropIndicator();
    }

    private void ResultsList_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("PinnedItemIndex"))
        {
            e.Effects = System.Windows.DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var pos = e.GetPosition(ResultsList);
        var targetIndex = GetDropTargetIndex(pos);

        if (targetIndex < 0 || targetIndex > _viewModel.PinnedItemCount)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            HideDropIndicator();
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            ShowDropIndicator(targetIndex);
        }

        e.Handled = true;
    }

    private void ResultsList_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        HideDropIndicator();
    }

    private void ResultsList_Drop(object sender, System.Windows.DragEventArgs e)
    {
        HideDropIndicator();

        if (!e.Data.GetDataPresent("PinnedItemIndex"))
            return;

        var fromIndex = (int)e.Data.GetData("PinnedItemIndex");
        var pos = e.GetPosition(ResultsList);
        var toIndex = GetDropTargetIndex(pos);

        if (toIndex < 0 || toIndex > _viewModel.PinnedItemCount)
            return;

        // Ajuster l'index cible si on déplace vers le bas
        if (toIndex > fromIndex)
            toIndex--;

        if (fromIndex != toIndex && toIndex >= 0 && toIndex < _viewModel.PinnedItemCount)
        {
            _viewModel.ReorderPinnedItem(fromIndex, toIndex);
        }
    }

    /// <summary>
    /// Trouve le ListBoxItem sous un point donné dans le ResultsList.
    /// </summary>
    private ListBoxItem? GetListBoxItemAtPoint(System.Windows.Point point)
    {
        var hitResult = VisualTreeHelper.HitTest(ResultsList, point);
        if (hitResult?.VisualHit == null) return null;

        DependencyObject? current = hitResult.VisualHit;
        while (current != null && current is not ListBoxItem)
            current = VisualTreeHelper.GetParent(current);

        return current as ListBoxItem;
    }

    /// <summary>
    /// Calcule l'index de destination du drop en fonction de la position de la souris.
    /// Retourne l'index *entre* les items (0 = avant le premier, N = après le dernier épinglé).
    /// </summary>
    private int GetDropTargetIndex(System.Windows.Point posInListBox)
    {
        var pinnedCount = _viewModel.PinnedItemCount;
        if (pinnedCount == 0) return -1;

        for (var i = 0; i < pinnedCount; i++)
        {
            var container = ResultsList.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem;
            if (container == null) continue;

            var itemPos = container.TranslatePoint(new System.Windows.Point(0, 0), ResultsList);
            var itemMid = itemPos.Y + container.ActualHeight / 2;

            if (posInListBox.Y < itemMid)
                return i;
        }

        return pinnedCount;
    }

    /// <summary>
    /// Affiche un indicateur visuel (ligne horizontale) à la position de drop.
    /// </summary>
    private void ShowDropIndicator(int insertIndex)
    {
        if (_dropIndicator == null)
        {
            _dropIndicator = new Border
            {
                Height = 2,
                CornerRadius = new CornerRadius(1),
                IsHitTestVisible = false,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch
            };
            _dropIndicator.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        }

        if (!DropIndicatorCanvas.Children.Contains(_dropIndicator))
            DropIndicatorCanvas.Children.Add(_dropIndicator);

        // Calculer la position Y de l'indicateur
        double targetY = 0;
        var pinnedCount = _viewModel.PinnedItemCount;

        if (insertIndex < pinnedCount)
        {
            var container = ResultsList.ItemContainerGenerator.ContainerFromIndex(insertIndex) as ListBoxItem;
            if (container != null)
            {
                var pos = container.TranslatePoint(new System.Windows.Point(0, 0), DropIndicatorCanvas);
                targetY = pos.Y;
            }
        }
        else if (pinnedCount > 0)
        {
            // Après le dernier épinglé
            var lastContainer = ResultsList.ItemContainerGenerator.ContainerFromIndex(pinnedCount - 1) as ListBoxItem;
            if (lastContainer != null)
            {
                var pos = lastContainer.TranslatePoint(new System.Windows.Point(0, lastContainer.ActualHeight), DropIndicatorCanvas);
                targetY = pos.Y;
            }
        }

        Canvas.SetTop(_dropIndicator, targetY - 1);
        Canvas.SetLeft(_dropIndicator, 8);
        _dropIndicator.Width = ResultsList.ActualWidth - 16;
        _dropIndicator.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Masque l'indicateur de drop.
    /// </summary>
    private void HideDropIndicator()
    {
        if (_dropIndicator != null)
            _dropIndicator.Visibility = Visibility.Collapsed;
    }

    #endregion

    #region Context Menu Handlers

    /// <summary>
    /// Ouvre le menu contextuel sur l'item actuellement sélectionné dans la liste.
    /// Utilisé par la touche Tab pour permettre la navigation 100% clavier.
    /// </summary>
    private void OpenContextMenuOnSelectedItem()
    {
        var container = ResultsList.ItemContainerGenerator.ContainerFromIndex(_viewModel.SelectedIndex) as ListBoxItem;
        if (container == null) return;

        // Récupérer ou créer le menu contextuel pour cet item
        var contextMenu = container.ContextMenu ?? FindResource("ResultContextMenu") as ContextMenu;
        if (contextMenu == null) return;

        contextMenu.PlacementTarget = container;
        contextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        contextMenu.IsOpen = true;
    }

    /// <summary>
    /// Génère dynamiquement le menu contextuel en fonction du résultat sélectionné.
    /// </summary>
    private void ResultContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu contextMenu)
            return;
        
        contextMenu.Items.Clear();
        
        // Récupérer le SearchResult depuis le ListBoxItem
        if (contextMenu.PlacementTarget is not ListBoxItem listBoxItem ||
            listBoxItem.DataContext is not SearchResult result)
            return;
        
        // Obtenir les actions disponibles pour ce résultat
        var isPinned = _settings.Search.IsPinned(result.Path);
        var hasAlias = _viewModel.HasAlias(result.Path);
        var actions = _fileActionProvider.GetActionsForResult(result, isPinned, hasAlias);
        
        // Créer le style pour les items
        var menuItemStyle = (Style)FindResource("DarkMenuItemStyle");
        
        int? lastCategory = null;
        
        foreach (var action in actions)
        {
            // Ajouter un séparateur entre les catégories
            var currentCategory = GetActionCategory(action.ActionType);
            if (lastCategory.HasValue && currentCategory != lastCategory.Value)
            {
                contextMenu.Items.Add(new Separator { Background = (System.Windows.Media.Brush)FindResource("BorderBrush") });
            }
            lastCategory = currentCategory;
            
            var menuItem = new MenuItem
            {
                Header = $"{action.Icon} {action.Name}",
                Style = menuItemStyle,
                InputGestureText = action.Shortcut,
                Tag = action
            };
            
            // Couleur spéciale pour Supprimer
            if (action.ActionType == FileActionType.Delete)
            {
                menuItem.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            }
            
            menuItem.Click += (s, args) =>
            {
                if (s is MenuItem mi && mi.Tag is FileAction fileAction)
                {
                    ExecuteContextAction(fileAction, result);
                }
            };
            
            contextMenu.Items.Add(menuItem);
        }
    }
    
    /// <summary>
    /// Détermine la catégorie d'une action pour le regroupement dans le menu.
    /// Retourne un int pour regrouper les actions visuellement avec des séparateurs.
    /// </summary>
    private static int GetActionCategory(FileActionType actionType)
    {
        return actionType switch
        {
            // Groupe 1: Lancement
            FileActionType.Open or FileActionType.OpenWith or FileActionType.RunAsAdmin 
                or FileActionType.OpenPrivate or FileActionType.EditInEditor => 1,
            // Groupe 2: Navigation
            FileActionType.OpenLocation or FileActionType.OpenInExplorer 
                or FileActionType.OpenInTerminal or FileActionType.OpenInVSCode => 2,
            // Groupe 3: Presse-papiers
            FileActionType.CopyPath or FileActionType.CopyName or FileActionType.CopyUrl => 3,
            // Groupe 4: Opérations
            FileActionType.Compress or FileActionType.SendByEmail => 4,
            // Groupe 5: Modification
            FileActionType.Rename or FileActionType.Delete or FileActionType.Properties => 5,
            // Groupe 6: Épingles & Alias
            FileActionType.CreateAlias or FileActionType.DeleteAlias or FileActionType.Pin or FileActionType.Unpin => 6,
            _ => 0
        };
    }
    
    /// <summary>
    /// Exécute une action du menu contextuel en déléguant au ViewModel.
    /// Point #1 : toute l'exécution passe par un chemin unique
    /// (ViewModel → ResultActionService → ActionOutcome).
    /// </summary>
    private void ExecuteContextAction(FileAction action, SearchResult result)
    {
        _viewModel.ExecuteActionOnResult(action, result);
    }

    #endregion
}
