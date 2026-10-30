namespace QuickLauncher.Services.CommandHandlers;

/// <summary>
/// Routeur central qui dispatche les requêtes vers le bon ICommandHandler.
/// Enregistré en singleton dans le conteneur DI.
/// 
/// Responsabilités:
/// - Déterminer quel handler peut traiter une requête
/// - Déléguer l'exécution au handler approprié
/// - Retourner null si aucun handler ne correspond (la requête sera traitée normalement)
/// </summary>
public sealed class CommandRouter
{
    private readonly IReadOnlyList<ICommandHandler> _handlers;
    
    public CommandRouter(IEnumerable<ICommandHandler> handlers)
    {
        // L'ordre est important : le premier handler qui match gagne
        _handlers = handlers.ToList().AsReadOnly();
    }
    
    /// <summary>
    /// Tente de router la requête vers un handler spécialisé.
    /// </summary>
    /// <param name="query">Requête de l'utilisateur, trimmée, casse ORIGINALE conservée
    /// (les handlers reconnaissent leur préfixe sans tenir compte de la casse).</param>
    /// <returns>Le handler approprié, ou null si la requête n'est pas une commande spéciale.</returns>
    public ICommandHandler? FindHandler(string query)
    {
        foreach (var handler in _handlers)
        {
            if (handler.CanHandle(query))
                return handler;
        }
        return null;
    }
    
    /// <summary>
    /// Raccourci : route et exécute en une seule opération.
    /// Retourne null si aucun handler ne correspond.
    /// </summary>
    public async Task<CommandResult?> TryExecuteAsync(string query, CancellationToken token)
    {
        var handler = FindHandler(query);
        if (handler == null)
            return null;
        
        return await handler.ExecuteAsync(query, token);
    }
}
