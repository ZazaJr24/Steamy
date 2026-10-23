using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Controls;

/// <summary>Request installed-game art only while a realized tile needs it.</summary>
public static class ArtworkLease
{
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<DependencyProperty, BindingBase>> SuspendedBindings = new();
    public static readonly DependencyProperty GameProperty = DependencyProperty.RegisterAttached("Game", typeof(Game), typeof(ArtworkLease), new PropertyMetadata(null, Changed));
    private static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached("Request", typeof(CancellationTokenSource), typeof(ArtworkLease));
    private static readonly DependencyProperty ViewportProperty = DependencyProperty.RegisterAttached("Viewport", typeof(IDisposable), typeof(ArtworkLease));
    private static readonly DependencyProperty PriorityProperty = DependencyProperty.RegisterAttached("Priority", typeof(ImagePriority), typeof(ArtworkLease), new PropertyMetadata(ImagePriority.Visible));
    private static readonly DependencyProperty ImagesActiveProperty = DependencyProperty.RegisterAttached("ImagesActive", typeof(bool), typeof(ArtworkLease), new PropertyMetadata(true));
    public static readonly DependencyProperty FeatureProperty = DependencyProperty.RegisterAttached("Feature", typeof(DashboardFeature), typeof(ArtworkLease), new PropertyMetadata(null, Changed));
    public static DashboardFeature? GetFeature(DependencyObject owner) => (DashboardFeature?)owner.GetValue(FeatureProperty);
    public static void SetFeature(DependencyObject owner, DashboardFeature? feature) => owner.SetValue(FeatureProperty, feature);
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached("Item", typeof(SteamCatalogItem), typeof(ArtworkLease), new PropertyMetadata(null, Changed));
    public static SteamCatalogItem? GetItem(DependencyObject owner) => (SteamCatalogItem?)owner.GetValue(ItemProperty);
    public static void SetItem(DependencyObject owner, SteamCatalogItem? item) => owner.SetValue(ItemProperty, item);
    public static Game? GetGame(DependencyObject owner) => (Game?)owner.GetValue(GameProperty);
    public static void SetGame(DependencyObject owner, Game? game) => owner.SetValue(GameProperty, game);
    private static void Changed(DependencyObject owner, DependencyPropertyChangedEventArgs args)
    {
        if (owner is not FrameworkElement view) return;
        view.Loaded -= Loaded; view.Unloaded -= Unloaded;
        view.Loaded += Loaded; view.Unloaded += Unloaded;
        if (view.IsLoaded) { Cancel(view); Loaded(view, new RoutedEventArgs()); }
    }
    private static void Unloaded(object sender, RoutedEventArgs args)
    { var view = (FrameworkElement)sender; Cancel(view); UpdateImagesActive(view, false); (view.GetValue(ViewportProperty) as IDisposable)?.Dispose(); view.ClearValue(ViewportProperty); }
    private static void Cancel(FrameworkElement view)
    { if (view.GetValue(RequestProperty) is CancellationTokenSource request) { request.Cancel(); request.Dispose(); view.ClearValue(RequestProperty); } }
    private static async void Loaded(object sender, RoutedEventArgs args)
    {
        var view = (FrameworkElement)sender;
        if (view.GetValue(ViewportProperty) is null) view.SetValue(ViewportProperty, ArtworkViewport.Observe(view, () => Loaded(view, new RoutedEventArgs())));
        if (ArtworkViewport.Priority(view) is not { } priority) { Cancel(view); UpdateImagesActive(view, false); return; }
        UpdateImagesActive(view, true);
        var existing = view.GetValue(RequestProperty) as CancellationTokenSource;
        if (existing is not null && !(priority == ImagePriority.Visible && (ImagePriority)view.GetValue(PriorityProperty) == ImagePriority.Prefetch)) return;
        var feature = GetFeature(view); var game = GetGame(view) ?? feature?.Game; var item = GetItem(view);
        if (game is null && item is null || game is not null && game.ArtworkImage is not null && game.HeaderImage is not null || item?.ArtworkImage is not null) return;
        var request = existing ?? new CancellationTokenSource(); var token = request.Token; view.SetValue(RequestProperty, request); view.SetValue(PriorityProperty, priority);
        try
        {
            if (feature is not null) await App.Services.GetRequiredService<IArtworkService>().LoadSpotlightCoverAsync(feature.Metadata, feature.Game, priority, token);
            else if (game is not null && priority == ImagePriority.Prefetch) await App.Services.GetRequiredService<IArtworkService>().PrefetchAsync([game], token);
            else if (game is not null) await App.Services.GetRequiredService<IArtworkService>().LoadAsync(game, token);
            else if (item is { AppId: > 0 } && priority == ImagePriority.Prefetch) await App.Services.GetRequiredService<ISteamCatalogService>().PrefetchArtworkAsync(item, token);
            else if (item is { AppId: > 0 }) await App.Services.GetRequiredService<ISteamCatalogService>().EnsureArtworkAsync(item, cancellationToken: token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is System.IO.IOException or System.Net.Http.HttpRequestException) { }
    }
    private static void UpdateImagesActive(FrameworkElement view, bool active)
    {
        if ((bool)view.GetValue(ImagesActiveProperty) == active) return;
        view.SetValue(ImagesActiveProperty, active);
        SetImagesActive(view, active);
    }
    // Keep binding definitions so recycled/offscreen tiles can restore their source;
    // the visual must not keep evicted weak-model artwork alive outside its viewport.
    internal static void SetImagesActive(DependencyObject parent, bool active)
    {
        // A cached page may become active while some of its realized tiles are still
        // outside the viewport. Their own lease restores them when they re-enter.
        if (active && parent.ReadLocalValue(ImagesActiveProperty) is false) return;
        static void Apply(DependencyObject owner, DependencyProperty property, bool enabled)
        {
            if (enabled)
            {
                if (SuspendedBindings.TryGetValue(owner, out var saved) && saved.Remove(property, out var definition))
                {
                    if (saved.Count == 0) SuspendedBindings.Remove(owner);
                    BindingOperations.SetBinding(owner, property, definition);
                }
                else BindingOperations.GetBindingExpression(owner, property)?.UpdateTarget();
            }
            else if (BindingOperations.GetBindingBase(owner, property) is { } definition)
            {
                SuspendedBindings.GetOrCreateValue(owner)[property] = definition;
                BindingOperations.ClearBinding(owner, property);
            }
        }
        if (parent is Image image) Apply(image, Image.SourceProperty, active);
        if (parent is ArtworkHalo halo) Apply(halo, ArtworkHalo.SourceProperty, active);
        if (parent is TrailerPreview trailer)
        {
            if (!active) trailer.StopPreview();
            Apply(trailer, TrailerPreview.PosterProperty, active);
        }
        var brush = parent switch { Border border => border.Background, Panel panel => panel.Background, Control control => control.Background, _ => null };
        if (brush is ImageBrush artwork && !artwork.IsFrozen) Apply(artwork, ImageBrush.ImageSourceProperty, active);
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++) SetImagesActive(VisualTreeHelper.GetChild(parent, index), active);
    }
}
