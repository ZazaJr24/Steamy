using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Steamy.Controls;

namespace Steamy.Pages;

public partial class LibraryPage
{
    private bool _galleryUpdateQueued;
    private bool _galleryResetPending;

    private void LibraryScroll_Changed(object sender, ScrollChangedEventArgs args)
    {
        if (sender is ScrollViewer scroll && ReferenceEquals(args.OriginalSource, scroll)) UpdateLibraryScrollChrome(scroll);
    }

    private void UpdateLibraryScrollChrome(ScrollViewer scroll)
    {
        // The first 64 scroll pixels consume the title's leading space. Translate
        // chrome with that same pixel offset; never animate row heights or resize
        // the virtualized viewport on every frame. Filters then stay reachable.
        var offset = scroll.VerticalOffset;
        var collapse = Math.Clamp(offset, 0, 64);
        LibraryHeadingSlide.Y = LibraryFilterSlide.Y = -collapse;
        LibraryHeading.Opacity = 1 - collapse / 64;
        LoadMoreOffset.Y = scroll.ExtentHeight - 100 + 28 - offset;
        var visible = scroll.ExtentHeight > 100 && LoadMoreOffset.Y < scroll.ViewportHeight && LoadMoreOffset.Y + 44 > 0;
        LoadMoreButton.Opacity = visible ? 1 : 0;
        LoadMoreButton.IsHitTestVisible = visible;
    }

    private void GalleryScroll_SizeChanged(object sender, SizeChangedEventArgs args)
    { if (sender is ScrollViewer scroll) UpdateLibraryScrollChrome(scroll); }

    private void LibraryFilterBar_SizeChanged(object sender, SizeChangedEventArgs args)
    {
        // Search hints and loading feedback can add a line. Reserve their real
        // height once it changes so covers never overlap the pinned filters.
        var margin = new Thickness(0, args.NewSize.Height + 24, 0, 0);
        LibraryGallery.Margin = LoadMoreButton.Margin = margin;
    }

    private void ResizeLibraryPresentation()
    {
        if (_installationOnly) return;
        LibrarySearchFrame.Width = ActualWidth < 1120 ? 240 : 306;
        LibraryTitleText.FontSize = 34;
    }

    private void GenresPrevious_Click(object sender, RoutedEventArgs args) => QuickGenresScroll.ScrollToHorizontalOffset(QuickGenresScroll.HorizontalOffset - Math.Max(120, QuickGenresScroll.ViewportWidth * .7));
    private void GenresNext_Click(object sender, RoutedEventArgs args) => QuickGenresScroll.ScrollToHorizontalOffset(QuickGenresScroll.HorizontalOffset + Math.Max(120, QuickGenresScroll.ViewportWidth * .7));
    private void Genres_MouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (QuickGenresScroll.ScrollableWidth <= 0) return;
        QuickGenresScroll.ScrollToHorizontalOffset(QuickGenresScroll.HorizontalOffset - Math.Sign(args.Delta) * 120);
        args.Handled = true;
    }
}

