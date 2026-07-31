using System.Diagnostics;
using System.Windows.Media;
using QuickLauncher.Models;

using Application = System.Windows.Application;

namespace QuickLauncher.Services;

/// <summary>
/// Service de chargement d'icônes natives pour les résultats de recherche.
/// Découple le modèle SearchResult de la logique d'extraction (IconExtractorService).
/// 
/// Amélioration #1 : Le SearchResult ne déclenche plus lui-même le chargement.
/// Amélioration #5 : Les chargements concurrents d'un même chemin sont mutualisés.
/// </summary>
public sealed class IconLoaderService : IIconLoader
{
    /// <summary>
    /// Extractions en cours, indexées par chemin.
    ///
    /// Deux recherches successives peuvent demander le même chemin avant que la première
    /// extraction soit terminée (frappe rapide). Partager la tâche plutôt que d'ignorer
    /// la seconde demande évite à la fois l'extraction en double ET le cas où le résultat
    /// le plus récent resterait sans icône parce qu'un chargement était déjà « en cours ».
    /// </summary>
    private readonly Dictionary<string, Task<ImageSource?>> _inFlight = [];
    private readonly object _lock = new();

    /// <summary>
    /// Types de résultats pour lesquels on charge une icône native.
    /// </summary>
    private static bool ShouldLoadIcon(ResultType type) => type is
        ResultType.Application or ResultType.StoreApp or ResultType.File or
        ResultType.Folder or ResultType.Script or ResultType.Bookmark;

    public async Task LoadIconsAsync(IReadOnlyList<SearchResult> results, CancellationToken cancellationToken = default)
    {
        // Associer chaque résultat éligible à l'extraction correspondante (nouvelle ou partagée)
        var pending = new List<(SearchResult Result, Task<ImageSource?> Load)>();

        foreach (var result in results)
        {
            if (cancellationToken.IsCancellationRequested) return;

            if (!ShouldLoadIcon(result.Type) || result.HasNativeIcon || string.IsNullOrEmpty(result.Path))
                continue;

            pending.Add((result, GetOrStartExtraction(result.Path)));
        }

        if (pending.Count == 0) return;

        // Appliquer chaque icône dès qu'elle est prête, indépendamment des autres
        await Task.WhenAll(pending.Select(p => ApplyWhenReadyAsync(p.Result, p.Load, cancellationToken)));
    }

    /// <summary>
    /// Retourne l'extraction en cours pour ce chemin, ou en démarre une.
    ///
    /// L'extraction est volontairement lancée avec <see cref="CancellationToken.None"/> :
    /// si elle était liée au jeton de la recherche qui l'a déclenchée, une frappe suivante
    /// annulerait une tâche que la nouvelle recherche est en train de réutiliser.
    /// L'extraction est courte et alimente les caches d'icônes — la laisser finir est bénéfique.
    /// </summary>
    private Task<ImageSource?> GetOrStartExtraction(string path)
    {
        lock (_lock)
        {
            if (_inFlight.TryGetValue(path, out var existing))
                return existing;

            var task = Task.Run(() => IconExtractorService.GetIcon(path));
            _inFlight[path] = task;

            // Retirer de la table dès la fin, succès ou échec
            _ = task.ContinueWith(
                _ => { lock (_lock) { _inFlight.Remove(path); } },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return task;
        }
    }

    /// <summary>
    /// Attend l'extraction puis publie l'icône sur le thread UI, sauf si la recherche
    /// a été annulée entre-temps.
    /// </summary>
    private static async Task ApplyWhenReadyAsync(SearchResult result, Task<ImageSource?> load, CancellationToken cancellationToken)
    {
        try
        {
            var icon = await load;

            if (icon == null || cancellationToken.IsCancellationRequested)
                return;

            // Dispatcher vers le thread UI pour mettre à jour la propriété bindée
            Application.Current?.Dispatcher.InvokeAsync(() => result.NativeIcon = icon);
        }
        catch (OperationCanceledException)
        {
            // Recherche annulée, normal
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[IconLoader] Error loading icon for '{result.Name}': {ex.Message}");
        }
    }
}
