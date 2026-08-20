using QuickLauncher.Services;
using Xunit;

namespace QuickLauncher.Tests;

/// <summary>
/// Cache LRU : éviction de l'entrée la moins récemment utilisée, promotion
/// au TryGet, et vidage.
/// </summary>
public class LruCacheTests
{
    [Fact]
    public void Set_puis_TryGet_retourne_la_valeur()
    {
        var cache = new LruCache<string, int>(3);
        cache.Set("a", 1);

        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(1, value);
    }

    [Fact]
    public void TryGet_sur_cle_absente_retourne_false()
    {
        var cache = new LruCache<string, int>(3);

        Assert.False(cache.TryGet("absent", out var value));
        Assert.Equal(0, value);
    }

    [Fact]
    public void Count_reflete_le_nombre_d_entrees()
    {
        var cache = new LruCache<string, int>(3);
        Assert.Equal(0, cache.Count);

        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Set_au_dela_de_la_capacite_evince_la_plus_ancienne()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("c", 3); // évince "a"

        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void TryGet_promeut_l_entree_et_la_protege_de_l_eviction()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);

        // "a" redevient la plus récemment utilisée : c'est "b" qui doit sauter.
        Assert.True(cache.TryGet("a", out _));
        cache.Set("c", 3);

        Assert.True(cache.TryGet("a", out var a));
        Assert.Equal(1, a);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("c", out _));
    }

    [Fact]
    public void Set_sur_cle_existante_met_a_jour_sans_augmenter_le_compte()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("a", 42);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal(42, value);
    }

    [Fact]
    public void Set_sur_cle_existante_promeut_l_entree()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        cache.Set("a", 10); // "a" repasse en tête, "b" devient la moins récente
        cache.Set("c", 3);

        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Clear_vide_le_cache()
    {
        var cache = new LruCache<string, int>(3);
        cache.Set("a", 1);
        cache.Set("b", 2);

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("b", out _));
    }

    [Fact]
    public void Clear_puis_Set_refonctionne_normalement()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Clear();
        cache.Set("b", 2);

        Assert.Equal(1, cache.Count);
        Assert.True(cache.TryGet("b", out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void Capacite_de_un_ne_garde_que_la_derniere_entree()
    {
        var cache = new LruCache<string, int>(1);
        cache.Set("a", 1);
        cache.Set("b", 2);

        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("b", out _));
    }

    [Fact]
    public void Capacite_invalide_leve_une_exception()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string, int>(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LruCache<string, int>(-1));
    }

    [Fact]
    public void Le_comparateur_fourni_est_respecte()
    {
        var cache = new LruCache<string, int>(3, StringComparer.OrdinalIgnoreCase);
        cache.Set("Notepad", 1);

        Assert.True(cache.TryGet("NOTEPAD", out var value));
        Assert.Equal(1, value);
    }
}
