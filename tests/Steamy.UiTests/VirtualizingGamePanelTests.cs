using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed class VirtualizingGamePanelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RealizesViewportScrollsToLastItemAndResetsOnFiltering(bool covers)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var control = (ItemsControl)XamlReader.Parse("""
                    <ItemsControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:c="clr-namespace:Steamy.Controls;assembly=Steamy" Width="900" Height="400">
                      <ItemsControl.Template><ControlTemplate TargetType="ItemsControl"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter /></ScrollViewer></ControlTemplate></ItemsControl.Template>
                      <ItemsControl.ItemsPanel><ItemsPanelTemplate><c:VirtualizingGamePanel /></ItemsPanelTemplate></ItemsControl.ItemsPanel>
                      <ItemsControl.ItemTemplate><DataTemplate><TextBlock Text="{Binding}" /></DataTemplate></ItemsControl.ItemTemplate>
                    </ItemsControl>
                    """);
                control.ItemsSource = Enumerable.Range(0, 1008).ToArray();
                void Layout() { control.Measure(new Size(900, 400)); control.Arrange(new Rect(0, 0, 900, 400)); control.UpdateLayout(); }
                Layout();
                var panel = FindPanel(control)!;
                Assert.NotNull(panel);
                if (covers)
                {
                    panel.MinItemWidth = 184; panel.MaxColumns = 6; panel.Spacing = 24;
                    panel.CoverRatio = 1.48; panel.FooterHeight = 80;
                    Layout();
                }
                Assert.InRange(panel.RealizedCount, 1, 40);
                Assert.NotNull(panel.ScrollOwner);
                panel.SetVerticalOffset(panel.ExtentHeight);
                Layout();
                Assert.True(panel.VerticalOffset > 0);
                Assert.InRange(panel.RealizedCount, 1, 40);
                Assert.NotNull(control.ItemContainerGenerator.ContainerFromIndex(1007));
                control.ItemsSource = new[] { 1 };
                Layout();
                Assert.Equal(1, panel.RealizedCount);
                control.ItemsSource = Array.Empty<int>();
                Layout();
                Assert.Equal(0, panel.RealizedCount);
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Virtualized gallery layout timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static VirtualizingGamePanel? FindPanel(DependencyObject parent)
    {
        if (parent is VirtualizingGamePanel panel) return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindPanel(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}
