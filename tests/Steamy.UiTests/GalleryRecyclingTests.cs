using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Steamy.Controls;

namespace Steamy.UiTests;

public sealed class GalleryRecyclingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepeatedLongScrollsReuseTilesAndRefreshTheirLabelsAndCommandTargets(bool covers) => OnSta(() =>
    {
        CountedButton.Created = 0;
        var items = new ObservableCollection<Entry>(Enumerable.Range(0, 1008).Select(i => new Entry(i, $"Game {i}")));
        var owner = Owner(); owner.ItemsSource = items;
        Layout(owner);
        var panel = Find<VirtualizingGamePanel>(owner);
        if (covers) { panel.MinItemWidth = 184; panel.MaxColumns = 6; panel.CoverRatio = 1.48; panel.FooterHeight = 80; Layout(owner); }
        var maximumRealized = panel.RealizedCount;
        var initialContainers = owner.CreatedContainers;
        var initialButtons = CountedButton.Created;
        for (var round = 0; round < 20; round++)
        {
            panel.SetVerticalOffset(round % 2 == 0 ? panel.ExtentHeight : 0);
            Layout(owner);
            maximumRealized = Math.Max(maximumRealized, panel.RealizedCount);
            Assert.InRange(panel.RealizedCount, 1, 40);
            VerifyRows(owner, panel);
        }
        // Both the containers and their actual template controls must be reused.
        // Merely caching empty containers would not reduce tile construction.
        Assert.InRange(owner.CreatedContainers - initialContainers, 0, maximumRealized);
        Assert.InRange(CountedButton.Created - initialButtons, 0, maximumRealized);
        panel.SetVerticalOffset(0); Layout(owner);
        items.Move(0, 7); Layout(owner); VerifyRows(owner, panel);
        items.Move(1, 900); Layout(owner); VerifyRows(owner, panel);
        items.Move(1007, 0); items.RemoveAt(3); items[1] = new Entry(2000, "Replaced title");
        Layout(owner); VerifyRows(owner, panel);
        Assert.Equal(items[0], owner.ItemContainerGenerator.ItemFromContainer(owner.ItemContainerGenerator.ContainerFromIndex(0)));
        owner.ItemsSource = new[] { new Entry(3000, "Filtered result") }; Layout(owner);
        Assert.Equal(1, panel.RealizedCount); VerifyRows(owner, panel);
        owner.ItemsSource = Array.Empty<Entry>(); Layout(owner);
        Assert.Equal(0, panel.RealizedCount);
        owner.ItemsSource = items; Layout(owner); VerifyRows(owner, panel);
    });

    [Fact]
    public void MixedOwnUiElementsKeepTheirIdentityInsteadOfEnteringTheGeneratedPool() => OnSta(() =>
    {
        var owner = Owner();
        owner.ItemsSource = Enumerable.Range(0, 1008).Select<int, object>(i => (i % 3) switch
        {
            0 => new TextBlock { Text = $"Own text {i}" },
            1 => new Button { Content = $"Own button {i}" },
            _ => new Entry(i, $"Generated {i}")
        }).ToArray();
        Layout(owner); var panel = Find<VirtualizingGamePanel>(owner);
        for (var round = 0; round < 6; round++)
        {
            panel.SetVerticalOffset(round % 2 == 0 ? panel.ExtentHeight : 0); Layout(owner);
            foreach (UIElement child in panel.Children)
            {
                var index = owner.ItemContainerGenerator.IndexFromContainer(child);
                Assert.InRange(index, 0, 1007);
                if (owner.Items[index] is UIElement original) Assert.Same(original, child);
            }
        }
    });

    private sealed record Entry(int Id, string Label);
    public sealed class CountedButton : Button
    {
        [ThreadStatic] public static int Created;
        public CountedButton() => Created++;
    }
    private sealed class CountedItems : ItemsControl
    {
        public int CreatedContainers;
        protected override DependencyObject GetContainerForItemOverride() { CreatedContainers++; return new ContentPresenter(); }
    }
    private static CountedItems Owner()
    {
        var button = new FrameworkElementFactory(typeof(CountedButton));
        button.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Entry.Label)));
        button.SetBinding(FrameworkElement.TagProperty, new Binding(nameof(Entry.Id)));
        button.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandParameterProperty, new Binding());
        var owner = new CountedItems { Width = 900, Height = 400, ItemTemplate = new DataTemplate { VisualTree = button } };
        VirtualizingPanel.SetVirtualizationMode(owner, VirtualizationMode.Recycling);
        owner.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="ItemsControl"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter /></ScrollViewer></ControlTemplate>
            """);
        owner.ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:c="clr-namespace:Steamy.Controls;assembly=Steamy"><c:VirtualizingGamePanel /></ItemsPanelTemplate>
            """);
        return owner;
    }
    private static void VerifyRows(ItemsControl owner, VirtualizingGamePanel panel)
    {
        foreach (UIElement child in panel.Children)
        {
            var index = owner.ItemContainerGenerator.IndexFromContainer(child);
            Assert.InRange(index, 0, owner.Items.Count - 1);
            var item = Assert.IsType<Entry>(owner.Items[index]);
            var button = Find<CountedButton>(child);
            Assert.Equal(item.Label, button.Content); Assert.Equal(item.Id, button.Tag); Assert.Same(item, button.CommandParameter);
        }
    }
    private static void Layout(FrameworkElement owner)
    {
        owner.Measure(new Size(900, 400)); owner.Arrange(new Rect(0, 0, 900, 400)); owner.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); owner.UpdateLayout();
    }
    private static T Find<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T match) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            try { return Find<T>(VisualTreeHelper.GetChild(parent, i)); } catch (InvalidOperationException) { }
        throw new InvalidOperationException($"Missing {typeof(T).Name}.");
    }
    private static void OnSta(Action check)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { check(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Gallery recycling timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
