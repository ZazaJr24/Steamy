using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed partial class VirtualizingGamePanelTests
{
    [Fact]
    public void ScrollingInsideTheSameRowsKeepsMeasurementValidAndMovesTheTiles()
    {
        OnWindowlessSta(() =>
        {
            var control = CreateWindowlessGrid(); Layout(control);
            var panel = FindPanel(control)!;
            var first = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(0);
            var position = first.TranslatePoint(new Point(), control);
            var count = panel.RealizedCount;
            panel.SetVerticalOffset(12);
            Assert.True(panel.IsMeasureValid, "A pixel offset inside the existing realization window must not restart tile generation/measurement.");
            Assert.True(panel.IsArrangeValid, "Retained rows move through one shared vector translation, without another arrange pass.");
            Layout(control);
            Assert.Same(first, control.ItemContainerGenerator.ContainerFromIndex(0));
            Assert.Equal(count, panel.RealizedCount);
            Assert.Equal(position.Y - 12, first.TranslatePoint(new Point(), control).Y, 3);
            Assert.Equal(12, panel.VerticalOffset, 3);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PixelFramesReuseTilesButRowCrossingAndReversalRefreshTheReserve(bool covers)
    {
        OnWindowlessSta(() =>
        {
            var control = CreateWindowlessGrid(); Layout(control);
            var panel = FindPanel(control)!;
            if (covers)
            {
                panel.MinItemWidth = 184; panel.MaxColumns = 6; panel.Spacing = 24;
                panel.CoverRatio = 1.48; panel.FooterHeight = 80; Layout(control);
            }
            var columns = Math.Clamp((int)((panel.ActualWidth + panel.Spacing) / (panel.MinItemWidth + panel.Spacing)), 1, panel.MaxColumns);
            var firstTile = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(0);
            var pitch = firstTile.ActualHeight + panel.Spacing;
            var start = 4.25 * pitch;
            panel.SetVerticalOffset(start); Layout(control);
            var firstIndex = 4 * columns;
            var retained = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(firstIndex);
            var initialY = retained.TranslatePoint(new Point(), control).Y;
            var count = panel.RealizedCount;
            var untilBoundary = Math.Min(pitch - start % pitch, pitch - (start + panel.ViewportHeight) % pitch);
            var distance = untilBoundary / 2;
            for (var frame = 1; frame <= 20; frame++)
            {
                var delta = distance * frame / 20;
                panel.SetVerticalOffset(start + delta);
                Assert.True(panel.IsMeasureValid);
                Layout(control);
                Assert.Equal(initialY - delta, retained.TranslatePoint(new Point(), control).Y, 3);
                Assert.Same(retained, control.ItemContainerGenerator.ContainerFromIndex(firstIndex));
                Assert.Equal(count, panel.RealizedCount);
            }
            panel.SetVerticalOffset(start + pitch);
            Assert.False(panel.IsMeasureValid);
            Layout(control);
            Assert.NotNull(control.ItemContainerGenerator.ContainerFromIndex(5 * columns));
            Assert.Null(control.ItemContainerGenerator.ContainerFromIndex(firstIndex));
            Assert.InRange(panel.RealizedCount, 1, 40);
            panel.SetVerticalOffset(panel.VerticalOffset - .5);
            Assert.False(panel.IsMeasureValid); // Backward reserve moves to the preceding row.
            Layout(control);
            Assert.NotNull(control.ItemContainerGenerator.ContainerFromIndex(firstIndex));
            panel.SetVerticalOffset(panel.VerticalOffset - .5);
            Assert.True(panel.IsMeasureValid);
            Layout(control);
        });
    }

    [Fact]
    public void CollectionAndTileSizeChangesStillInvalidateMeasurementWhileScrolling()
    {
        OnWindowlessSta(() =>
        {
            var control = CreateWindowlessGrid();
            var items = new ObservableCollection<int>(Enumerable.Range(0, 1008));
            control.ItemsSource = items; Layout(control);
            var panel = FindPanel(control)!;
            panel.SetVerticalOffset(400); Layout(control);
            items.RemoveAt(0);
            panel.SetVerticalOffset(401);
            Assert.False(panel.IsMeasureValid); Layout(control);
            items.Move(12, 16); Layout(control);
            for (var index = 0; index < items.Count; index++)
                if (control.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement tile)
                    Assert.Equal(items[index], tile.DataContext);
            panel.CoverRatio = 1.48;
            panel.SetVerticalOffset(402);
            Assert.False(panel.IsMeasureValid); Layout(control);
            Assert.InRange(panel.RealizedCount, 1, 40);
            Assert.True(panel.ExtentHeight > 4000);
            control.ItemsSource = new[] { 17 }; Layout(control);
            // A single tall cover can still exceed the viewport. Clamp to its
            // real remaining scroll range rather than assuming it fits at zero.
            Assert.Equal(Math.Min(402, Math.Max(0, panel.ExtentHeight - panel.ViewportHeight)), panel.VerticalOffset, 3);
            Assert.Equal(1, panel.RealizedCount);
            Assert.NotNull(control.ItemContainerGenerator.ContainerFromIndex(0));
        });
    }

    [Fact]
    public void TranslatedTilesKeepHitTargetsAndExternalTransformsUseLayoutFallback()
    {
        OnWindowlessSta(() =>
        {
            var control = CreateWindowlessGrid();
            control.ItemTemplate = (DataTemplate)XamlReader.Parse("""
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Border Background="Transparent"><TextBlock Text="{Binding}" /></Border></DataTemplate>
                """);
            Layout(control);
            var panel = FindPanel(control)!;
            var tile = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(0);
            panel.SetVerticalOffset(12); Layout(control);
            var point = tile.TranslatePoint(new Point(10, 30), control);
            // Detached controls are not IsVisible for routed input; exercise the
            // visual hit geometry without creating or activating an HWND.
            var hit = VisualTreeHelper.HitTest(control, point)?.VisualHit;
            Assert.NotNull(hit);
            Assert.True(ReferenceEquals(tile, hit) || tile.IsAncestorOf(hit));
            panel.SetVerticalOffset(0); Layout(control);
            Assert.Equal(0, ((TranslateTransform)panel.RenderTransform).Y);
            panel.RenderTransform = new TranslateTransform(7, 0); Layout(control);
            panel.SetVerticalOffset(12);
            Assert.False(panel.IsMeasureValid && panel.IsArrangeValid);
            Layout(control);
            Assert.Equal(7, ((TranslateTransform)panel.RenderTransform).X);
            Assert.Equal(0, ((TranslateTransform)panel.RenderTransform).Y);
        });
    }

    [Fact]
    public void WheelBurstChangesDestinationWithoutScrollingBetweenRenderFrames()
    {
        OnWindowlessSta(() =>
        {
            var viewer = new ScrollViewer { Width = 300, Height = 160, Content = new Border { Height = 3000 } };
            viewer.Measure(new Size(300, 160)); viewer.Arrange(new Rect(0, 0, 300, 160)); viewer.UpdateLayout();
            SmoothScroll.SetEnabled(viewer, true);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var property = (DependencyProperty)typeof(SmoothScroll).GetField("StateProperty", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
            var state = viewer.GetValue(property);
            var animate = state.GetType().GetMethod("AnimateTo", flags)!;
            try
            {
                for (var i = 1; i <= 32; i++)
                {
                    animate.Invoke(state, new object[] { i * 54d }); viewer.UpdateLayout();
                    Assert.Equal(0, viewer.VerticalOffset);
                }
                Assert.Equal(32 * 54d, (double)state.GetType().GetField("_target", flags)!.GetValue(state)!);
            }
            finally { SmoothScroll.SetEnabled(viewer, false); }
        });
    }

    private static ItemsControl CreateWindowlessGrid()
    {
        var control = (ItemsControl)XamlReader.Parse("""
            <ItemsControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:c="clr-namespace:Steamy.Controls;assembly=Steamy" Width="900" Height="400" VirtualizingPanel.VirtualizationMode="Recycling">
              <ItemsControl.Template><ControlTemplate TargetType="ItemsControl"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter /></ScrollViewer></ControlTemplate></ItemsControl.Template>
              <ItemsControl.ItemsPanel><ItemsPanelTemplate><c:VirtualizingGamePanel /></ItemsPanelTemplate></ItemsControl.ItemsPanel>
              <ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding}" /></DataTemplate></ItemsControl.ItemTemplate>
            </ItemsControl>
            """);
        control.ItemsSource = Enumerable.Range(0, 1008).ToArray();
        return control;
    }
    private static void Layout(ItemsControl control)
    { control.Measure(new Size(900, 400)); control.Arrange(new Rect(0, 0, 900, 400)); control.UpdateLayout(); }
    private static void OnWindowlessSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Windowless grid test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
