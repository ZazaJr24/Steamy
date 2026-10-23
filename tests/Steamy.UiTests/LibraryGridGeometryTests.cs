using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace Steamy.UiTests;

public sealed partial class VirtualizingGamePanelTests
{
    [Theory]
    [InlineData(980, 4)]
    [InlineData(1440, 6)]
    [InlineData(1920, 6)]
    public void LibraryHeaderCaptionSpacingAndAppendRemainVirtualized(int windowWidth, int columns)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var width = Math.Min(1408, windowWidth - 64);
                var control = (ItemsControl)XamlReader.Parse("""
                    <ItemsControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:c="clr-namespace:Steamy.Controls;assembly=Steamy" VirtualizingPanel.VirtualizationMode="Recycling">
                      <ItemsControl.Template><ControlTemplate TargetType="ItemsControl"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Hidden"><ItemsPresenter /></ScrollViewer></ControlTemplate></ItemsControl.Template>
                      <ItemsControl.ItemsPanel><ItemsPanelTemplate><c:VirtualizingGamePanel MinItemWidth="184" MaxColumns="6" Spacing="26" RowSpacing="32" CoverRatio="1.3333333333" FooterHeight="76" LeadingHeight="0" TrailingHeight="100" /></ItemsPanelTemplate></ItemsControl.ItemsPanel>
                      <ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding}" /></DataTemplate></ItemsControl.ItemTemplate>
                    </ItemsControl>
                    """);
                var items = new ObservableCollection<int>(Enumerable.Range(1, 48));
                control.ItemsSource = items;
                void Layout() { control.Measure(new Size(width, 640)); control.Arrange(new Rect(0, 0, width, 640)); control.UpdateLayout(); }
                Layout();
                var panel = FindPanel(control)!;
                var first = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(0);
                var nextRow = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(columns);
                var firstPoint = first.TransformToAncestor(panel).Transform(new Point());
                var nextPoint = nextRow.TransformToAncestor(panel).Transform(new Point());
                Assert.Equal(0, firstPoint.Y, 3);
                Assert.Equal(0, nextPoint.X, 3);
                Assert.Equal(first.ActualHeight + 32, nextPoint.Y - firstPoint.Y, 3);
                Assert.Equal((width - (columns - 1) * 26d) / columns, first.ActualWidth, 3);
                Assert.InRange(panel.RealizedCount, 1, columns * 3);
                panel.SetVerticalOffset(350); Layout();
                var offset = panel.VerticalOffset;
                var retained = control.ItemContainerGenerator.ContainerFromIndex(columns);
                foreach (var item in Enumerable.Range(49, 48)) items.Add(item);
                Layout();
                Assert.Equal(offset, panel.VerticalOffset);
                Assert.Same(retained, control.ItemContainerGenerator.ContainerFromIndex(columns));
                panel.SetVerticalOffset(double.MaxValue); Layout();
                Assert.NotNull(control.ItemContainerGenerator.ContainerFromIndex(95));
                Assert.InRange(panel.RealizedCount, 1, columns * 3);
                Assert.Equal(panel.ExtentHeight - panel.ViewportHeight, panel.VerticalOffset, 3);
            }
            catch (Exception exception) { failure = exception; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Windowless library layout timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
