using System.Buffers;
using System.Runtime.CompilerServices;
using QuickLauncher.Models;

namespace QuickLauncher.Services;

/// <summary>
/// Algorithmes de recherche avancée pour QuickLauncher.
/// Implémente Damerau-Levenshtein (typos + transpositions), fuzzy matching per-word style fzf,
/// et scoring basé sur la fréquence d'utilisation.
/// 
/// Optimisations:
/// - Cache LRU pour les calculs de distance
/// - ArrayPool (zéro allocation heap pour les petites chaînes)
/// - ReadOnlySpan&lt;char&gt; pour éviter les allocations de sous-chaînes
/// - Fuzzy per-word: chaque mot de la requête est matché contre chaque mot cible
/// </summary>
public static class SearchAlgorithms
{
    /// <summary>
    /// Cache LRU thread-safe pour les calculs de distance.
    /// </summary>
    private static readonly LruCache<(string, string), int> _distanceCache = new(10_000);

    #region Damerau-Levenshtein Distance

    /// <summary>
    /// Calcule la distance de Damerau-Levenshtein entre deux chaînes.
    /// Contrairement au Levenshtein classique, gère les transpositions de caractères adjacents
    /// (ex: "firfox" → "firefox" = distance 1 au lieu de 2).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int DamerauLevenshteinDistance(string source, string target)
    {
        if (string.IsNullOrEmpty(source)) return target?.Length ?? 0;
        if (string.IsNullOrEmpty(target)) return source.Length;
        
        var s = source.ToLowerInvariant();
        var t = target.ToLowerInvariant();
        
        // Amélioration #6 : ne pas cacher les requêtes < 3 chars.
        // Pour un launcher où l'on tape lettre par lettre, les paires éphémères
        // (1-2 chars) polluent le cache LRU sans bénéfice réel.
        if (s.Length < 3 && t.Length < 3)
            return ComputeDamerauLevenshtein(s.AsSpan(), t.AsSpan());
        
        var key = (s, t);
        if (_distanceCache.TryGet(key, out var cached))
            return cached;

        var result = ComputeDamerauLevenshtein(s.AsSpan(), t.AsSpan());
        _distanceCache.Set(key, result);
        return result;
    }

    /// <summary>
    /// Implémentation optimisée de Damerau-Levenshtein (Optimal String Alignment) avec :
    /// - ArrayPool pour zéro allocation GC
    /// - Optimisations préfixe/suffixe commun
    /// - Gestion des transpositions de caractères adjacents
    /// </summary>
    internal static int ComputeDamerauLevenshtein(ReadOnlySpan<char> source, ReadOnlySpan<char> target)
    {
        var sLen = source.Length;
        var tLen = target.Length;

        if (sLen == 0) return tLen;
        if (tLen == 0) return sLen;
        if (source.SequenceEqual(target)) return 0;

        // Optimisation: préfixe commun
        var prefixLen = 0;
        var minLen = Math.Min(sLen, tLen);
        while (prefixLen < minLen && source[prefixLen] == target[prefixLen])
            prefixLen++;

        // Optimisation: suffixe commun
        var suffixLen = 0;
        while (suffixLen < sLen - prefixLen && 
               suffixLen < tLen - prefixLen &&
               source[sLen - 1 - suffixLen] == target[tLen - 1 - suffixLen])
            suffixLen++;

        var src = source.Slice(prefixLen, sLen - prefixLen - suffixLen);
        var tgt = target.Slice(prefixLen, tLen - prefixLen - suffixLen);
        
        var n = src.Length;
        var m = tgt.Length;

        if (n == 0) return m;
        if (m == 0) return n;

        // Damerau-Levenshtein nécessite 3 lignes (previous, current, et pre-previous pour transpositions)
        var poolSize = (m + 1) * 3;
        var pool = ArrayPool<int>.Shared.Rent(poolSize);
        
        try
        {
            var prevPrevRow = pool.AsSpan(0, m + 1);
            var prevRow = pool.AsSpan(m + 1, m + 1);
            var currRow = pool.AsSpan((m + 1) * 2, m + 1);

            for (var j = 0; j <= m; j++)
                prevRow[j] = j;

            for (var i = 1; i <= n; i++)
            {
                currRow[0] = i;
                var srcChar = src[i - 1];

                for (var j = 1; j <= m; j++)
                {
                    var cost = srcChar == tgt[j - 1] ? 0 : 1;

                    // Insertion, Suppression, Substitution
                    currRow[j] = Math.Min(
                        Math.Min(currRow[j - 1] + 1, prevRow[j] + 1),
                        prevRow[j - 1] + cost);

                    // Transposition (si les 2 derniers chars sont échangés)
                    if (i > 1 && j > 1 &&
                        src[i - 1] == tgt[j - 2] &&
                        src[i - 2] == tgt[j - 1])
                    {
                        currRow[j] = Math.Min(currRow[j], prevPrevRow[j - 2] + cost);
                    }
                }

                // Rotation des lignes
                var temp = prevPrevRow;
                prevPrevRow = prevRow;
                prevRow = currRow;
                currRow = temp;
            }

            return prevRow[m];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(pool);
        }
    }

    /// <summary>
    /// Wrapper de compatibilité — appelle Damerau-Levenshtein.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int LevenshteinDistance(string source, string target)
        => DamerauLevenshteinDistance(source, target);

    /// <summary>
    /// Calcule la similarité entre deux chaînes (0.0 à 1.0) via Damerau-Levenshtein.
    /// </summary>
    public static double LevenshteinSimilarity(string source, string target)
    {
        if (string.IsNullOrEmpty(source) && string.IsNullOrEmpty(target)) return 1.0;
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return 0.0;

        var distance = DamerauLevenshteinDistance(source, target);
        var maxLength = Math.Max(source.Length, target.Length);
        
        return 1.0 - (double)distance / maxLength;
    }

    #endregion

    #region Fuzzy Matching Amélioré
    
    private static readonly ScoringWeights DefaultWeights = new();

    /// <summary>
    /// Séparateurs utilisés pour découper les noms en mots.
    /// </summary>
    private static readonly char[] WordSeparators = [' ', '-', '_', '.', '+', '(', ')'];
    
    /// <summary>
    /// Dictionnaire d'abréviations communes pour améliorer la recherche.
    /// </summary>
    private static readonly Dictionary<string, string[]> CommonAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        // Développement
        ["vs"] = ["visual studio", "visual studio code"],
        ["vsc"] = ["visual studio code"],
        ["vscode"] = ["visual studio code"],
        ["code"] = ["visual studio code"],
        ["np"] = ["notepad"],
        ["npp"] = ["notepad++"],
        ["notepad"] = ["notepad++"],
        ["sublime"] = ["sublime text"],
        ["st"] = ["sublime text"],
        ["idea"] = ["intellij"],
        ["ws"] = ["webstorm"],
        
        // Navigateurs
        ["ff"] = ["firefox", "mozilla firefox"],
        ["chrome"] = ["google chrome", "chrome"],
        ["gc"] = ["google chrome"],
        ["edge"] = ["microsoft edge"],
        ["brave"] = ["brave browser"],
        ["opera"] = ["opera gx", "opera browser"],
        
        // Microsoft Office
        ["word"] = ["microsoft word"],
        ["excel"] = ["microsoft excel"],
        ["ppt"] = ["powerpoint", "microsoft powerpoint"],
        ["powerpoint"] = ["microsoft powerpoint"],
        ["outlook"] = ["microsoft outlook"],
        ["onenote"] = ["microsoft onenote"],
        ["teams"] = ["microsoft teams"],
        
        // Adobe
        ["ps"] = ["photoshop", "adobe photoshop"],
        ["ai"] = ["illustrator", "adobe illustrator"],
        ["ae"] = ["after effects", "adobe after effects"],
        ["pr"] = ["premiere", "premiere pro", "adobe premiere"],
        ["id"] = ["indesign", "adobe indesign"],
        ["lr"] = ["lightroom", "adobe lightroom"],
        ["xd"] = ["adobe xd"],
        ["acrobat"] = ["adobe acrobat"],
        
        // Utilitaires Windows
        ["cmd"] = ["command prompt", "invite de commandes", "cmd.exe"],
        ["wt"] = ["windows terminal", "terminal"],
        ["term"] = ["windows terminal", "terminal"],
        ["calc"] = ["calculator", "calculatrice"],
        ["paint"] = ["mspaint", "paint 3d"],
        ["explorer"] = ["file explorer", "explorateur de fichiers"],
        ["snip"] = ["snipping tool", "outil capture"],
        ["task"] = ["task manager", "gestionnaire des tâches", "taskmgr"],
        
        // Autres applications courantes
        ["vlc"] = ["vlc media player"],
        ["obs"] = ["obs studio"],
        ["discord"] = ["discord"],
        ["slack"] = ["slack"],
        ["zoom"] = ["zoom"],
        ["spotify"] = ["spotify"],
        ["steam"] = ["steam"],
        ["git"] = ["git bash", "github desktop"],
        ["gh"] = ["github desktop"],
        ["docker"] = ["docker desktop"],
        ["postman"] = ["postman"],
        ["insomnia"] = ["insomnia"],
        ["figma"] = ["figma"],
        ["notion"] = ["notion"],
        ["evernote"] = ["evernote"],
        ["7z"] = ["7-zip"],
        ["zip"] = ["7-zip", "winzip", "winrar"],
        ["rar"] = ["winrar"],
    };

    /// <summary>
    /// Effectue un fuzzy match amélioré avec scoring détaillé.
    /// </summary>
    public static int CalculateFuzzyScore(string query, string target, int useCount = 0)
        => CalculateFuzzyScore(query, target, useCount, null, DefaultWeights);
    
    /// <summary>
    /// Effectue un fuzzy match amélioré avec scoring détaillé et poids configurables.
    /// </summary>
    public static int CalculateFuzzyScore(string query, string target, int useCount, ScoringWeights weights)
        => CalculateFuzzyScore(query, target, useCount, null, weights);
    
    /// <summary>
    /// Pipeline de scoring principal.
    /// Ordre: Exact → StartsWith → Contains → Initiales → Sous-séquence → Fuzzy per-word → Fuzzy global
    /// Le fuzzy per-word est le cœur de la tolérance aux typos.
    /// </summary>
    public static int CalculateFuzzyScore(string query, string target, int useCount, DateTime? lastUsed,
        ScoringWeights weights, Dictionary<string, string[]>? userAbbreviations = null,
        string? targetNormalized = null)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(target))
            return 0;

        weights ??= DefaultWeights;

        var queryLower = query.ToLowerInvariant();
        // Strip les emojis du nom pour ne pas polluer le fuzzy matching
        // Ex: "⚙️ Paramètres Windows" → "paramètres windows"
        // targetNormalized (précalculé sur SearchResult) évite de refaire ce travail à chaque frappe.
        var targetLower = targetNormalized ?? StripEmojis(target).ToLowerInvariant();

        int score = 0;
        
        // 0. Abréviations : user d'abord (priorité), puis built-in
        var matchedAbbreviation = false;
        if (userAbbreviations != null && userAbbreviations.TryGetValue(queryLower, out var userTargets))
        {
            foreach (var possibleTarget in userTargets)
            {
                if (targetLower.Contains(possibleTarget, StringComparison.OrdinalIgnoreCase))
                {
                    score = weights.ExactMatch - 100;
                    matchedAbbreviation = true;
                    break;
                }
            }
        }
        if (!matchedAbbreviation && CommonAbbreviations.TryGetValue(queryLower, out var builtinTargets))
        {
            foreach (var possibleTarget in builtinTargets)
            {
                if (targetLower.Contains(possibleTarget))
                {
                    score = weights.ExactMatch - 100;
                    matchedAbbreviation = true;
                    break;
                }
            }
        }
        
        if (matchedAbbreviation)
            return ApplyBonuses(score, useCount, lastUsed, weights);

        // 1. Correspondance exacte
        if (targetLower == queryLower)
        {
            score = weights.ExactMatch;
        }
        // 2. Commence par la requête
        else if (targetLower.StartsWith(queryLower))
        {
            score = weights.StartsWith + (queryLower.Length * 10);
        }
        // 3. Contient la requête
        else if (targetLower.Contains(queryLower))
        {
            var index = targetLower.IndexOf(queryLower);
            score = weights.Contains - (index * 5);
        }
        // 4. Match par initiales (ex: "vs" → "Visual Studio")
        else if (MatchesInitials(queryLower, targetLower))
        {
            score = weights.InitialsMatch;
        }
        // 5. Match par sous-séquence (tous les caractères dans l'ordre)
        else if (IsSubsequenceMatch(queryLower, targetLower, weights, out var subScore))
        {
            score = weights.SubsequenceMatch + subScore;
        }
        
        // 6. Fuzzy per-word: chaque mot de la requête est matché fuzzy contre les mots cibles.
        //    S'exécute en complément (peut améliorer un score existant) ou en fallback.
        var fuzzyPerWordScore = CalculateFuzzyPerWordScore(queryLower, targetLower, weights);
        if (fuzzyPerWordScore > score)
            score = fuzzyPerWordScore;
        
        // 7. Fuzzy global sur le nom complet (fallback ultime pour les requêtes à un seul mot)
        if (score == 0)
        {
            score = CalculateFuzzyGlobalScore(queryLower, targetLower, weights);
        }

        if (score <= 0)
            return 0;

        // 8. Bonus pour les mots qui matchent exactement
        var queryWords = queryLower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        var targetWords = targetLower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var qWord in queryWords)
        {
            if (targetWords.Any(tw => tw == qWord))
                score += weights.ExactWordBonus;
        }

        return ApplyBonuses(score, useCount, lastUsed, weights);
    }

    /// <summary>
    /// Fuzzy matching per-word : découpe la requête et la cible en mots,
    /// puis trouve le meilleur appariement fuzzy entre chaque mot de la requête et les mots cibles.
    /// 
    /// Ex: "firfox" → ["firfox"] vs "Mozilla Firefox" → ["mozilla", "firefox"]
    ///     "firfox" ≈ "firefox" (DL distance = 1, similarité 0.86) → score élevé
    /// 
    /// Ex multi-mots: "visul studo" → ["visul", "studo"] vs "Visual Studio" → ["visual", "studio"]
    ///     "visul" ≈ "visual" (distance 1), "studo" ≈ "studio" (distance 1) → très bon score
    /// </summary>
    private static int CalculateFuzzyPerWordScore(string queryLower, string targetLower, ScoringWeights weights)
    {
        var queryWords = queryLower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        var targetWords = targetLower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        
        if (queryWords.Length == 0 || targetWords.Length == 0)
            return 0;
        
        // Pour chaque mot de la requête, trouver le meilleur match parmi les mots cibles
        double totalSimilarity = 0;
        var matchedCount = 0;
        var usedTargetIndices = new HashSet<int>();
        
        foreach (var qWord in queryWords)
        {
            // Requête trop courte pour un fuzzy fiable — exiger un match exact/prefix
            if (qWord.Length < 2)
                continue;
            
            var bestSimilarity = 0.0;
            var bestTargetIndex = -1;
            
            for (var i = 0; i < targetWords.Length; i++)
            {
                if (usedTargetIndices.Contains(i))
                    continue;
                
                var tWord = targetWords[i];
                
                // Match exact du mot
                if (qWord == tWord)
                {
                    bestSimilarity = 1.0;
                    bestTargetIndex = i;
                    break;
                }
                
                // Le mot cible commence par le mot requête
                if (tWord.StartsWith(qWord))
                {
                    var sim = weights.WordPrefixSimilarity;
                    if (sim > bestSimilarity)
                    {
                        bestSimilarity = sim;
                        bestTargetIndex = i;
                    }
                    continue;
                }
                
                // Le mot requête commence par le mot cible (requête plus longue que la cible)
                if (qWord.StartsWith(tWord))
                {
                    var sim = weights.WordReversePrefixSimilarity;
                    if (sim > bestSimilarity)
                    {
                        bestSimilarity = sim;
                        bestTargetIndex = i;
                    }
                    continue;
                }
                
                // Fuzzy via Damerau-Levenshtein
                // Seuil adaptatif: mots courts → tolérer 1 erreur, mots longs → proportionnel
                var maxDistance = qWord.Length <= 4 ? 1 : (int)Math.Ceiling(qWord.Length * weights.FuzzyWordMaxDistanceFactor);
                var distance = DamerauLevenshteinDistance(qWord, tWord);
                
                if (distance <= maxDistance)
                {
                    var sim = 1.0 - (double)distance / Math.Max(qWord.Length, tWord.Length);
                    if (sim > bestSimilarity)
                    {
                        bestSimilarity = sim;
                        bestTargetIndex = i;
                    }
                }
            }
            
            if (bestTargetIndex >= 0)
            {
                matchedCount++;
                totalSimilarity += bestSimilarity;
                usedTargetIndices.Add(bestTargetIndex);
            }
        }
        
        // Tous les mots significatifs de la requête doivent matcher
        var significantWords = queryWords.Count(w => w.Length >= 2);
        if (significantWords == 0 || matchedCount < significantWords)
            return 0;
        
        // Score basé sur la similarité moyenne
        var avgSimilarity = totalSimilarity / matchedCount;
        
        // Barème:
        // avgSimilarity 1.0 (parfait)  → score ≈ Contains (600) car on a déjà matché exact/startsWith plus haut
        // avgSimilarity 0.85           → score ≈ 400
        // avgSimilarity 0.7            → score ≈ 250 (fuzzy)
        var baseScore = (int)(avgSimilarity * weights.FuzzyPerWordMultiplier);
        
        // Bonus si tous les mots de la requête ont matché (pas de résidus)
        if (matchedCount == queryWords.Length)
            baseScore += weights.FuzzyPerWordAllMatchedBonus;
        
        // Bonus quand le nombre de mots requête ≈ nombre de mots cible (match complet)
        if (queryWords.Length >= targetWords.Length)
            baseScore += weights.FuzzyPerWordFullCoverageBonus;
        
        return baseScore;
    }
    
    /// <summary>
    /// Fuzzy matching global sur le nom complet.
    /// Fallback pour les cas où le per-word ne fonctionne pas
    /// (ex: requête sans espace sur un nom sans espace).
    /// </summary>
    private static int CalculateFuzzyGlobalScore(string queryLower, string targetLower, ScoringWeights weights)
    {
        // Similarité globale sur tout le nom
        var similarity = LevenshteinSimilarity(queryLower, targetLower);
        if (similarity >= weights.FuzzyMatchThreshold)
        {
            return (int)(similarity * weights.FuzzyMatchMultiplier);
        }
        
        // Essayer contre chaque mot individuel de la cible
        if (queryLower.Length >= 3)
        {
            var targetWords = targetLower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
            var bestScore = 0;
            
            foreach (var word in targetWords)
            {
                var wordSim = LevenshteinSimilarity(queryLower, word);
                if (wordSim >= weights.FuzzyMatchThreshold)
                {
                    var wordScore = (int)(wordSim * weights.FuzzyMatchMultiplier * weights.FuzzyGlobalWordMultiplier);
                    bestScore = Math.Max(bestScore, wordScore);
                }
            }
            
            return bestScore;
        }
        
        return 0;
    }

    /// <summary>
    /// Applique les bonus d'usage et de recency au score.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ApplyBonuses(int score, int useCount, DateTime? lastUsed, ScoringWeights weights)
    {
        if (useCount > 0)
            score += Math.Min(useCount * weights.UsageBonusPerUse, weights.MaxUsageBonus);
        
        if (weights.EnableRecencyBonus && lastUsed.HasValue && lastUsed.Value > DateTime.MinValue)
        {
            var daysSince = (DateTime.UtcNow - lastUsed.Value).TotalDays;
            score += Math.Max(0, weights.MaxRecencyBonus - (int)(daysSince * weights.RecencyDecayPerDay));
        }
        
        return score;
    }

    /// <summary>
    /// Vérifie si la requête correspond aux initiales du texte cible.
    /// Ex: "vs" → "Visual Studio", "gc" → "Google Chrome"
    /// </summary>
    internal static bool MatchesInitials(string query, string target)
    {
        var words = target.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < query.Length) return false;

        // Initiales strictes
        var initials = string.Concat(words.Where(w => w.Length > 0).Select(w => char.ToLower(w[0])));
        if (initials.StartsWith(query)) return true;

        // Initiales avec majuscules internes (CamelCase)
        var camelInitials = new System.Text.StringBuilder();
        foreach (var word in words)
        {
            camelInitials.Append(char.ToLower(word[0]));
            for (int i = 1; i < word.Length; i++)
            {
                if (char.IsUpper(word[i]))
                    camelInitials.Append(char.ToLower(word[i]));
            }
        }
        
        return camelInitials.ToString().Contains(query);
    }

    /// <summary>
    /// Vérifie si la requête est une sous-séquence du texte cible avec scoring.
    /// Ex: "vsc" → "Visual Studio Code"
    /// </summary>
    internal static bool IsSubsequenceMatch(string query, string target, ScoringWeights weights, out int matchScore)
    {
        matchScore = 0;
        var queryIndex = 0;
        var consecutiveMatches = 0;
        var lastMatchIndex = -2;

        for (var i = 0; i < target.Length && queryIndex < query.Length; i++)
        {
            if (target[i] == query[queryIndex])
            {
                queryIndex++;
                
                if (i == lastMatchIndex + 1)
                {
                    consecutiveMatches++;
                    matchScore += consecutiveMatches * weights.ConsecutiveMatchBonus;
                }
                else
                {
                    consecutiveMatches = 0;
                }
                
                if (i == 0 || !char.IsLetterOrDigit(target[i - 1]))
                    matchScore += weights.WordBoundaryBonus;
                
                lastMatchIndex = i;
            }
        }

        if (queryIndex == query.Length)
        {
            var spread = lastMatchIndex - (target.Length - query.Length);
            matchScore += Math.Max(0, 50 - spread);
            return true;
        }

        matchScore = 0;
        return false;
    }

    #endregion

    #region Path Fuzzy Matching
    
    /// <summary>
    /// Calcule un score de fuzzy matching multi-mots sur un chemin complet.
    /// Permet de trouver "proj quick" → C:\Projects\QuickLauncher
    /// </summary>
    public static int CalculatePathFuzzyScore(string query, string fullPath, ScoringWeights? weights = null)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(fullPath))
            return 0;
        
        weights ??= DefaultWeights;
        
        var queryWords = query.ToLowerInvariant()
            .Split([' '], StringSplitOptions.RemoveEmptyEntries);
        
        if (queryWords.Length < 1)
            return 0;
        
        // Découper le chemin en segments
        var pathSegments = fullPath.ToLowerInvariant()
            .Split(['\\', '/', '-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        
        if (queryWords.Length == 1)
        {
            var pathOnly = System.IO.Path.GetDirectoryName(fullPath) ?? fullPath;
            var segments = pathOnly.ToLowerInvariant()
                .Split(['\\', '/', '-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
            
            var word = queryWords[0];
            var bestScore = 0;
            
            foreach (var seg in segments)
            {
                if (seg == word)
                    bestScore = Math.Max(bestScore, weights.PathExactSegmentMatch);
                else if (seg.StartsWith(word))
                    bestScore = Math.Max(bestScore, (int)(weights.PathExactSegmentMatch * 0.7));
                else if (seg.Contains(word))
                    bestScore = Math.Max(bestScore, (int)(weights.PathExactSegmentMatch * 0.4));
                else if (word.Length >= 3)
                {
                    var sim = LevenshteinSimilarity(word, seg);
                    if (sim >= weights.PathFuzzySegmentThreshold)
                        bestScore = Math.Max(bestScore, (int)(weights.PathExactSegmentMatch * sim * 0.4));
                }
            }
            
            return bestScore;
        }
        
        // Chaque mot de la requête doit matcher au moins un segment
        var totalScore = 0;
        var matchedWords = 0;
        
        foreach (var word in queryWords)
        {
            var bestWordScore = 0;
            
            foreach (var segment in pathSegments)
            {
                int wordScore;
                
                if (segment == word)
                    wordScore = weights.PathExactSegmentMatch;
                else if (segment.StartsWith(word))
                    wordScore = (int)(weights.PathExactSegmentMatch * 0.8);
                else if (segment.Contains(word))
                    wordScore = (int)(weights.PathExactSegmentMatch * 0.5);
                else if (word.Length >= 3)
                {
                    var similarity = LevenshteinSimilarity(word, segment);
                    if (similarity >= weights.PathFuzzySegmentThreshold)
                        wordScore = (int)(weights.PathExactSegmentMatch * similarity * 0.4);
                    else
                        continue;
                }
                else
                    continue;
                
                bestWordScore = Math.Max(bestWordScore, wordScore);
            }
            
            if (bestWordScore > 0)
            {
                matchedWords++;
                totalScore += bestWordScore;
            }
        }
        
        if (matchedWords < queryWords.Length)
            return 0;
        
        totalScore += weights.PathAllWordsMatchBonus;
        return totalScore / queryWords.Length;
    }
    
    #endregion

    #region Description Matching

    /// <summary>
    /// Séparateurs Unicode reconnus comme emojis ou symboles décoratifs.
    /// Utilisé pour nettoyer les noms avant le fuzzy matching.
    /// </summary>
    private static readonly HashSet<System.Globalization.UnicodeCategory> EmojiCategories =
    [
        System.Globalization.UnicodeCategory.OtherSymbol,        // ⚙️🔊🔍 etc.
        System.Globalization.UnicodeCategory.ModifierSymbol,
        System.Globalization.UnicodeCategory.NonSpacingMark,     // Variation selectors (️)
        System.Globalization.UnicodeCategory.Format,             // Zero-width joiners
    ];

    /// <summary>
    /// Retire les emojis et symboles décoratifs d'un texte pour améliorer le fuzzy matching.
    /// Gère correctement les surrogate pairs (emojis hors BMP comme 🔊, 🖥️, etc.).
    /// Ex: "⚙️ Paramètres Windows" → "Paramètres Windows"
    /// </summary>
    public static string StripEmojis(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            // Variation Selector-16 (️) : toujours skipper
            if (text[i] == '\uFE0F')
                continue;
            
            // Surrogate pair : décoder le codepoint complet
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var rune = char.ConvertToUtf32(text[i], text[i + 1]);
                var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune);
                if (EmojiCategories.Contains(category))
                {
                    i++; // Skipper la paire complète
                    continue;
                }
                sb.Append(text[i]);
                sb.Append(text[i + 1]);
                i++;
                continue;
            }
            
            // Caractère BMP simple
            var charCategory = char.GetUnicodeCategory(text[i]);
            if (!EmojiCategories.Contains(charCategory) && !char.IsSurrogate(text[i]))
                sb.Append(text[i]);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Calcule un score de correspondance sur la description d'un résultat.
    /// Permet de trouver des paramètres Windows via leurs mots-clés de description.
    /// Ex: "DNS" → match sur la description "Adaptateurs réseau, IP, DNS"
    /// </summary>
    public static int CalculateDescriptionScore(string query, string description, ScoringWeights? weights = null)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(description))
            return 0;

        weights ??= DefaultWeights;
        var queryLower = query.ToLowerInvariant();
        var descLower = description.ToLowerInvariant();

        // Match exact de la requête complète dans la description
        if (descLower.Contains(queryLower))
        {
            // Bonus si le match est au début d'un mot
            var index = descLower.IndexOf(queryLower);
            var atWordBoundary = index == 0 || !char.IsLetterOrDigit(descLower[index - 1]);
            return atWordBoundary ? weights.DescriptionExactAtBoundary : weights.DescriptionExactInline;
        }

        // Multi-mots: chaque mot de la requête doit matcher quelque part dans la description
        var queryWords = queryLower.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);

        if (queryWords.Length > 1)
        {
            var allMatch = queryWords.All(w => w.Length < 2 || descLower.Contains(w));
            if (allMatch)
                return weights.DescriptionAllWordsMatch;
        }

        // Match d'au moins un mot significatif (>= 3 chars)
        var matchedWords = queryWords.Count(w => w.Length >= 3 && descLower.Contains(w));
        if (matchedWords > 0)
        {
            return weights.DescriptionPartialWordBase + (matchedWords * weights.DescriptionPartialWordBonus);
        }

        // Fuzzy matching par mot (tolérance aux typos dans la description)
        if (queryWords.Length == 1 && queryLower.Length >= 4)
        {
            var descWords = descLower.Split(
                [' ', ',', '•', '(', ')', '-', '_', '/'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var dWord in descWords)
            {
                if (dWord.Length < 3) continue;
                var maxDist = queryLower.Length <= 5 ? 1 : 2;
                var dist = DamerauLevenshteinDistance(queryLower, dWord);
                if (dist <= maxDist)
                {
                    var sim = 1.0 - (double)dist / Math.Max(queryLower.Length, dWord.Length);
                    return (int)(sim * weights.DescriptionFuzzyMultiplier);
                }
            }
        }

        return 0;
    }

    #endregion

    #region Utilitaires

    /// <summary>
    /// Vide le cache de distance.
    /// </summary>
    public static void ClearCache() => _distanceCache.Clear();

    /// <summary>
    /// Nombre d'entrées dans le cache.
    /// </summary>
    public static int CacheCount => _distanceCache.Count;

    #endregion
}
