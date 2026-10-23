using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Steamy.Controls;

public partial class ControllerDiagram : UserControl
{
    public static readonly DependencyProperty SelectedSourceProperty = DependencyProperty.Register(nameof(SelectedSource), typeof(string), typeof(ControllerDiagram),
        new FrameworkPropertyMetadata("Cross", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (view, _) => ((ControllerDiagram)view).RefreshSelection()));
    public string SelectedSource { get => (string)GetValue(SelectedSourceProperty); set => SetValue(SelectedSourceProperty, value); }
    public ControllerDiagram()
    {
        InitializeComponent();
        ApplyTemplate();
        Measure(new Size(640, 300));
        RefreshSelection();
    }
    private void SelectButton(object sender, RoutedEventArgs args)
    {
        if (sender is ToggleButton { Tag: string source }) SetCurrentValue(SelectedSourceProperty, source);
        RefreshSelection();
    }
    private void RefreshSelection()
    {
        if (Diagram is null) return;
        foreach (var button in Diagram.Children.OfType<ToggleButton>()) button.IsChecked = Equals(button.Tag, SelectedSource);
    }
}
