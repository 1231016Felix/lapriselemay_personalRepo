using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using QuickLauncher.Models;

namespace QuickLauncher.Views;

/// <summary>
/// Encapsule toute la logique d'animation de la fenêtre launcher :
/// show/hide (5 styles), stagger des items de résultat, GPU caching.
/// 
/// Extrait de LauncherWindow.xaml.cs pour responsabilité unique.
/// Le code-behind ne fait plus que déléguer à cette classe.
/// </summary>
public sealed class WindowAnimationHelper
{
    // ── Éléments UI ciblés ──
    private readonly FrameworkElement _mainBorder;
    private readonly FrameworkElement _shadowBorder;
    private readonly TranslateTransform _mainBorderTranslate;
    private readonly ScaleTransform _mainBorderScale;
    private readonly Func<AppSettings> _getSettings;

    // ── État d'animation ──
    private int _hideGeneration;
    private bool _isAnimatingHide;
    private bool _isShowAnimating;
    private int _searchGeneration;

    /// <summary>True pendant l'animation de hide (empêche un double hide).</summary>
    public bool IsAnimatingHide => _isAnimatingHide;

    /// <summary>True pendant l'animation de show (désactive le stagger des items).</summary>
    public bool IsShowAnimating => _isShowAnimating;

    /// <summary>Génération de recherche courante pour invalider les stagger obsolètes.</summary>
    public int SearchGeneration => _searchGeneration;

    /// <summary>Incrémente la génération de recherche (appelé quand Results est vidée).</summary>
    public void IncrementSearchGeneration() => _searchGeneration++;

    // ── Constantes & ressources partagées ──
    private const int AnimationFrameRate = 60;

    private static readonly IEasingFunction EaseOut;
    private static readonly IEasingFunction EaseIn;
    private static readonly IEasingFunction BounceOut;
    private static readonly BitmapCache SharedGpuCache;

    static WindowAnimationHelper()
    {
        var easeOut = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        easeOut.Freeze();
        EaseOut = easeOut;

        var easeIn = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        easeIn.Freeze();
        EaseIn = easeIn;

        var bounceOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.2 };
        bounceOut.Freeze();
        BounceOut = bounceOut;

        SharedGpuCache = new BitmapCache { EnableClearType = false, RenderAtScale = 1 };
        SharedGpuCache.Freeze();
    }

    public WindowAnimationHelper(
        FrameworkElement mainBorder,
        FrameworkElement shadowBorder,
        TranslateTransform mainBorderTranslate,
        ScaleTransform mainBorderScale,
        Func<AppSettings> getSettings)
    {
        _mainBorder = mainBorder;
        _shadowBorder = shadowBorder;
        _mainBorderTranslate = mainBorderTranslate;
        _mainBorderScale = mainBorderScale;
        _getSettings = getSettings;
    }

    private AppSettings Settings => _getSettings();
    private Duration AnimDuration => new(TimeSpan.FromMilliseconds(Settings.Appearance.AnimationDurationMs));
    private Duration ItemAnimDuration => new(TimeSpan.FromMilliseconds(Math.Max(50, Settings.Appearance.AnimationDurationMs - 20)));

    // ═══════════════════════════════════════════════════════
    //  SHOW / HIDE FENÊTRE
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Animation d'apparition selon le style configuré.
    /// Invalide tout hide en cours pour éviter que son Completed ne cache la fenêtre.
    /// </summary>
    public void PlayShowAnimation()
    {
        _hideGeneration++;
        _isAnimatingHide = false;
        _isShowAnimating = true;

        ClearAllAnimations();

        if (!Settings.Appearance.EnableAnimations)
        {
            _mainBorder.Opacity = 1;
            _shadowBorder.Opacity = 1;
            _mainBorderTranslate.Y = 0;
            _mainBorderScale.ScaleX = 1;
            _mainBorderScale.ScaleY = 1;
            _isShowAnimating = false;
            return;
        }

        _mainBorder.CacheMode = SharedGpuCache;
        var dur = AnimDuration;

        _shadowBorder.BeginAnimation(UIElement.OpacityProperty, MakeAnim(0, 1, dur, TimeSpan.Zero, EaseOut));

        DoubleAnimation opacityAnim;

        switch (Settings.Appearance.AnimationStyle)
        {
            case AnimationStyle.FadeSlide:
                _mainBorder.Opacity = 0;
                _mainBorderTranslate.Y = -6;
                opacityAnim = MakeAnim(0, 1, dur, TimeSpan.Zero, EaseOut);
                _mainBorderTranslate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(-6, 0, dur, TimeSpan.Zero, EaseOut));
                break;

            case AnimationStyle.Fade:
                _mainBorder.Opacity = 0;
                _mainBorderTranslate.Y = 0;
                opacityAnim = MakeAnim(0, 1, dur, TimeSpan.Zero, EaseOut);
                break;

            case AnimationStyle.Scale:
                _mainBorder.Opacity = 0;
                _mainBorderTranslate.Y = 0;
                _mainBorderScale.ScaleX = 0.95;
                _mainBorderScale.ScaleY = 0.95;
                opacityAnim = MakeAnim(0, 1, dur, TimeSpan.Zero, EaseOut);
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.95, 1, dur, TimeSpan.Zero, EaseOut));
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.95, 1, dur, TimeSpan.Zero, EaseOut));
                break;

            case AnimationStyle.Slide:
                _mainBorder.Opacity = 1;
                _mainBorderTranslate.Y = -8;
                opacityAnim = MakeAnim(1, 1, dur, TimeSpan.Zero, null);
                _mainBorderTranslate.BeginAnimation(TranslateTransform.YProperty, MakeAnim(-8, 0, dur, TimeSpan.Zero, EaseOut));
                break;

            case AnimationStyle.Pop:
                _mainBorder.Opacity = 0;
                _mainBorderTranslate.Y = 0;
                _mainBorderScale.ScaleX = 0.88;
                _mainBorderScale.ScaleY = 0.88;
                opacityAnim = MakeAnim(0, 1, dur, TimeSpan.Zero, EaseOut);
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, MakeAnim(0.88, 1, dur, TimeSpan.Zero, BounceOut));
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.88, 1, dur, TimeSpan.Zero, BounceOut));
                break;

            default:
                opacityAnim = MakeAnim(0, 1, dur, TimeSpan.Zero, EaseOut);
                break;
        }

        opacityAnim.Completed += (_, _) =>
        {
            _mainBorder.CacheMode = null;
            _isShowAnimating = false;
        };
        _mainBorder.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
    }

    /// <summary>
    /// Animation de disparition selon le style configuré.
    /// <paramref name="onCompleted"/> est appelé quand l'animation se termine
    /// (la fenêtre doit y appeler Hide + Reset).
    /// </summary>
    public void PlayHideAnimation(Action onCompleted)
    {
        if (_isAnimatingHide) return;

        if (!Settings.Appearance.EnableAnimations)
        {
            ClearAllAnimations();
            ResetToHiddenState();
            onCompleted();
            return;
        }

        _isAnimatingHide = true;
        var gen = _hideGeneration;
        var dur = AnimDuration;

        _mainBorder.CacheMode = SharedGpuCache;
        _shadowBorder.BeginAnimation(UIElement.OpacityProperty,
            MakeAnim(_shadowBorder.Opacity, 0, dur, TimeSpan.Zero, EaseIn));

        DoubleAnimation pilot;

        switch (Settings.Appearance.AnimationStyle)
        {
            case AnimationStyle.FadeSlide:
                pilot = MakeHidePilot(_mainBorder.Opacity, 0, dur, gen, onCompleted);
                _mainBorder.BeginAnimation(UIElement.OpacityProperty, pilot);
                _mainBorderTranslate.BeginAnimation(TranslateTransform.YProperty,
                    MakeAnim(_mainBorderTranslate.Y, -4, dur, TimeSpan.Zero, EaseIn));
                break;

            case AnimationStyle.Fade:
                pilot = MakeHidePilot(_mainBorder.Opacity, 0, dur, gen, onCompleted);
                _mainBorder.BeginAnimation(UIElement.OpacityProperty, pilot);
                break;

            case AnimationStyle.Scale:
                pilot = MakeHidePilot(_mainBorder.Opacity, 0, dur, gen, onCompleted);
                _mainBorder.BeginAnimation(UIElement.OpacityProperty, pilot);
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    MakeAnim(_mainBorderScale.ScaleX, 0.95, dur, TimeSpan.Zero, EaseIn));
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    MakeAnim(_mainBorderScale.ScaleY, 0.95, dur, TimeSpan.Zero, EaseIn));
                break;

            case AnimationStyle.Slide:
                pilot = MakeHidePilot(0, 0, dur, gen, onCompleted);
                _mainBorderTranslate.BeginAnimation(TranslateTransform.YProperty,
                    MakeAnim(_mainBorderTranslate.Y, -8, dur, TimeSpan.Zero, EaseIn));
                _mainBorder.BeginAnimation(UIElement.OpacityProperty, pilot);
                break;

            case AnimationStyle.Pop:
                pilot = MakeHidePilot(_mainBorder.Opacity, 0, dur, gen, onCompleted);
                _mainBorder.BeginAnimation(UIElement.OpacityProperty, pilot);
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    MakeAnim(_mainBorderScale.ScaleX, 0.88, dur, TimeSpan.Zero, EaseIn));
                _mainBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    MakeAnim(_mainBorderScale.ScaleY, 0.88, dur, TimeSpan.Zero, EaseIn));
                break;

            default:
                pilot = MakeHidePilot(1, 0, dur, gen, onCompleted);
                _mainBorder.BeginAnimation(UIElement.OpacityProperty, pilot);
                break;
        }
    }

    /// <summary>
    /// Annule toutes les animations et remet l'état caché immédiatement (pas d'animation).
    /// </summary>
    public void HideImmediate()
    {
        _hideGeneration++;
        _isAnimatingHide = false;
        ClearAllAnimations();
        ResetToHiddenState();
        _mainBorder.CacheMode = null;
    }

    // ═══════════════════════════════════════════════════════
    //  STAGGER DES ITEMS DE RÉSULTAT
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Anime l'apparition d'un ListBoxItem avec stagger progressif.
    /// Appelé depuis ResultItem_Loaded dans le code-behind.
    /// </summary>
    public void AnimateResultItem(ListBoxItem item, int index, System.Windows.Controls.ListBox resultsList)
    {
        // S'assurer que l'item est visible (containers recyclés)
        item.BeginAnimation(UIElement.OpacityProperty, null);
        item.Opacity = 1;

        if (!Settings.Appearance.EnableAnimations || _isShowAnimating)
            return;

        if (index < 0) index = 0;
        var generation = _searchGeneration;
        var dur = ItemAnimDuration;
        var staggerMs = Math.Max(0, Settings.Appearance.StaggerDelayMs);
        var beginTime = TimeSpan.FromMilliseconds(index * staggerMs);

        // Le TransformGroup vient d'un Setter de Style : WPF gèle (seals) les
        // Freezable déclarés dans un Style et les partage entre TOUS les
        // conteneurs. BeginAnimation lançait donc « Impossible d'animer la
        // propriété 'Y' [...] car l'objet est sealed ou gelé » à chaque item
        // chargé — exception avalée par le gestionnaire global, d'où des
        // animations de liste mortes et un journal saturé.
        // On donne à chaque conteneur son propre groupe modifiable.
        if (item.RenderTransform is not TransformGroup tg || tg.IsFrozen || tg.Children.Count < 2)
        {
            tg = new TransformGroup();
            tg.Children.Add(new ScaleTransform(1, 1));
            tg.Children.Add(new TranslateTransform(0, 0));
            item.RenderTransform = tg;
        }

        var scale = tg.Children.OfType<ScaleTransform>().FirstOrDefault();
        var translate = tg.Children.OfType<TranslateTransform>().FirstOrDefault();

        item.CacheMode = SharedGpuCache;

        DoubleAnimation? pilotAnim = null;

        switch (Settings.Appearance.AnimationStyle)
        {
            case AnimationStyle.FadeSlide:
            case AnimationStyle.Slide:
                var slideFrom = Settings.Appearance.AnimationStyle == AnimationStyle.FadeSlide ? 4.0 : 6.0;
                pilotAnim = MakeAnim(slideFrom, 0, dur, beginTime, EaseOut);
                translate?.BeginAnimation(TranslateTransform.YProperty, pilotAnim);
                break;

            case AnimationStyle.Scale:
                pilotAnim = MakeAnim(0.96, 1, dur, beginTime, EaseOut);
                scale?.BeginAnimation(ScaleTransform.ScaleXProperty, pilotAnim);
                scale?.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.96, 1, dur, beginTime, EaseOut));
                break;

            case AnimationStyle.Pop:
                pilotAnim = MakeAnim(0.90, 1, dur, beginTime, BounceOut);
                scale?.BeginAnimation(ScaleTransform.ScaleXProperty, pilotAnim);
                scale?.BeginAnimation(ScaleTransform.ScaleYProperty, MakeAnim(0.90, 1, dur, beginTime, BounceOut));
                break;

            case AnimationStyle.Fade:
            default:
                pilotAnim = MakeAnim(0, 0, dur, beginTime, null);
                break;
        }

        if (pilotAnim != null)
        {
            pilotAnim.Completed += (_, _) =>
            {
                if (generation == _searchGeneration)
                    item.CacheMode = null;
            };
        }
    }

    // ═══════════════════════════════════════════════════════
    //  HELPERS INTERNES
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// Annule toutes les animations en cours sur les éléments ciblés.
    /// </summary>
    public void ClearAllAnimations()
    {
        _mainBorder.BeginAnimation(UIElement.OpacityProperty, null);
        _mainBorderTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        _mainBorderScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _mainBorderScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _shadowBorder.BeginAnimation(UIElement.OpacityProperty, null);
    }

    /// <summary>
    /// Remet l'état visuel « caché » sans animation.
    /// </summary>
    public void ResetToHiddenState()
    {
        _mainBorder.Opacity = 0;
        _shadowBorder.Opacity = 0;
        _mainBorderTranslate.Y = -6;
        _mainBorderScale.ScaleX = 1;
        _mainBorderScale.ScaleY = 1;
    }

    private DoubleAnimation MakeHidePilot(double from, double to, Duration duration,
        int generation, Action onCompleted)
    {
        var anim = new DoubleAnimation(from, to, duration) { EasingFunction = EaseIn };
        Timeline.SetDesiredFrameRate(anim, AnimationFrameRate);

        anim.Completed += (_, _) =>
        {
            if (generation != _hideGeneration) return;

            ClearAllAnimations();
            ResetToHiddenState();
            _mainBorder.CacheMode = null;
            _isAnimatingHide = false;
            onCompleted();
        };

        return anim;
    }

    private static DoubleAnimation MakeAnim(double from, double to, Duration duration,
        TimeSpan beginTime, IEasingFunction? easing)
    {
        var anim = new DoubleAnimation(from, to, duration)
        {
            BeginTime = beginTime,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, AnimationFrameRate);
        return anim;
    }
}