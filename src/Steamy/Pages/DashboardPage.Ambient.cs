using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Steamy.Models;
using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.Pages;

public partial class DashboardPage
{
    internal double ToolbarRegionLuminance(Rect region)
    {
        var fallback = DashboardBackdrop.RegionLuminance(region);
        var posters = ChromePosters(UpcomingGameGrid).Where(poster => poster.IsVisible && poster.Poster is not null && poster.ActualWidth > 0 && poster.ActualHeight > 0)
            .Select(poster => (Poster: poster, Bounds: new Rect(poster.TranslatePoint(new Point(), DashboardBackdrop), poster.RenderSize))).ToArray();
        double sum = 0;
        for (var row = 0; row < 3; row++)
        for (var column = 0; column < 7; column++)
        {
            var point = new Point(region.X + region.Width * (column + .5) / 7, region.Y + region.Height * (row + .5) / 3);
            var match = posters.FirstOrDefault(item => item.Bounds.Contains(point));
            sum += match.Poster is not null && ArtworkColorSample.TryLuminance(match.Poster.Poster!,
                (point.X - match.Bounds.X) / match.Bounds.Width, (point.Y - match.Bounds.Y) / match.Bounds.Height,
                match.Bounds.Width / match.Bounds.Height,out var luminance) ? luminance : fallback;
        }
        return sum / 21;
    }
    private static IEnumerable<Steamy.Controls.TrailerPreview> ChromePosters(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Steamy.Controls.TrailerPreview poster) yield return poster;
            else foreach (var nested in ChromePosters(child)) yield return nested;
        }
    }
    private FrameworkElement? _previewMouseTarget, _previewFocusTarget;
    private DashboardFeature? _previewFeature;
    private Game? _previewGame;

    private void DashboardAmbient_Enter(object sender, MouseEventArgs args)
    { _previewMouseTarget = (FrameworkElement)sender; RefreshDashboardPreview(); }
    private void DashboardAmbient_Leave(object sender, MouseEventArgs args)
    { if (ReferenceEquals(sender, _previewMouseTarget)) _previewMouseTarget = null; RefreshDashboardPreview(); }
    private void DashboardAmbient_Focus(object sender, KeyboardFocusChangedEventArgs args)
    { _previewFocusTarget = (FrameworkElement)sender; RefreshDashboardPreview(); }
    private void DashboardAmbient_Unloaded(object sender, RoutedEventArgs args)
    {
        if (!ReferenceEquals(sender, _previewMouseTarget) && !ReferenceEquals(sender, _previewFocusTarget)) return;
        if (ReferenceEquals(sender, _previewMouseTarget)) _previewMouseTarget = null;
        if (ReferenceEquals(sender, _previewFocusTarget)) _previewFocusTarget = null;
        RefreshDashboardPreview();
    }
    private void DashboardAmbient_Blur(object sender, KeyboardFocusChangedEventArgs args)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (sender is FrameworkElement { IsKeyboardFocusWithin: false } && ReferenceEquals(sender, _previewFocusTarget)) _previewFocusTarget = null;
            RefreshDashboardPreview();
        });
    }
    private void PreviewArtworkChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Game.HeaderImage) or nameof(Game.ArtworkImage) or nameof(DashboardFeature.HeroArtwork)) RefreshDashboardPreview();
    }
    private void RefreshDashboardPreview()
    {
        if (!IsLoaded) return;
        if (!UiThemeService.AmbientEnabled) { DashboardBackdrop.SetPreviewArtwork(null); return; }
        var feature = (_previewMouseTarget ?? _previewFocusTarget)?.DataContext as DashboardFeature;
        var game = feature?.Game;
        if (!ReferenceEquals(_previewFeature, feature))
        {
            if (_previewFeature is not null) _previewFeature.PropertyChanged -= PreviewArtworkChanged;
            _previewFeature = feature;
            if (feature is not null) feature.PropertyChanged += PreviewArtworkChanged;
        }
        if (!ReferenceEquals(_previewGame, game))
        {
            if (_previewGame is not null) _previewGame.PropertyChanged -= PreviewArtworkChanged;
            _previewGame = game;
            if (game is not null) game.PropertyChanged += PreviewArtworkChanged;
        }
        var previewTop = UpcomingSection.TranslatePoint(new Point(), DashboardBackdrop).Y;
        if ((_previewMouseTarget ?? _previewFocusTarget) is { IsLoaded: true } target)
            previewTop = Math.Max(previewTop, target.TranslatePoint(new Point(), DashboardBackdrop).Y - DashboardBackdrop.HeroHeight * .3);
        DashboardBackdrop.SetPreviewArtwork(feature?.HeroArtwork ?? game?.HeaderImage ?? game?.ArtworkImage, previewTop);
    }
    private void ClearDashboardPreview()
    {
        if (_previewFeature is not null) _previewFeature.PropertyChanged -= PreviewArtworkChanged;
        if (_previewGame is not null) _previewGame.PropertyChanged -= PreviewArtworkChanged;
        _previewFeature = null; _previewGame = null; _previewMouseTarget = _previewFocusTarget = null;
        DashboardBackdrop.SetPreviewArtwork(null);
    }
    private void UpdateDashboardBodyColors()
    {
        var highContrast = UiThemeService.IsHighContrastActive;
        var light = UiThemeService.AmbientEnabled ? UiThemeService.IsLight : UiThemeService.StaticBackgroundIsLight;
        BrushResources.Set(Resources, "DashboardBodyForeground", highContrast ? SystemColors.WindowTextBrush : light ? Brushes.Black : Brushes.White);
        if (highContrast)
        {
            BrushResources.Set(Resources, "DashboardBodySecondary", SystemColors.WindowTextBrush);
            BrushResources.Set(Resources, "DashboardBodyTertiary", SystemColors.GrayTextBrush);
        }
        else
        {
            BrushResources.SetColor(Resources, "DashboardBodySecondary", light ? Color.FromRgb(40, 40, 40) : Color.FromRgb(214, 214, 214));
            BrushResources.SetColor(Resources, "DashboardBodyTertiary", light ? Color.FromRgb(60, 60, 60) : Color.FromRgb(184, 184, 184));
        }
    }
}
