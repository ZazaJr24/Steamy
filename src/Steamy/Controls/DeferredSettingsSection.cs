using System.Windows;
using System.Windows.Controls;

namespace Steamy.Controls;

/// <summary>Constructs a settings category only when selected or matched by search.</summary>
public sealed class DeferredSettingsSection : Decorator
{
    public static readonly DependencyProperty SectionTemplateProperty = DependencyProperty.Register(
        nameof(SectionTemplate), typeof(DataTemplate), typeof(DeferredSettingsSection));
    public DataTemplate? SectionTemplate { get => (DataTemplate?)GetValue(SectionTemplateProperty); set => SetValue(SectionTemplateProperty, value); }
    public string SearchLabels { get; set; } = string.Empty;
    public string ExportedNames { get; set; } = string.Empty;
    public bool IsDiscordSection { get; set; }
    internal bool IsRealized => Child is not null;

    internal void EnsureContent(FrameworkElement namesOwner)
    {
        if (Child is not null || SectionTemplate is null) return;
        var content = (FrameworkElement)SectionTemplate.LoadContent();
        Child = content;
        // Preserve the page's authored names for commands and accessibility
        // checks, while the template keeps unvisited controls unconstructed.
        foreach (var name in ExportedNames.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var element = content.Name == name ? content : content.FindName(name);
            if (element is not null && namesOwner.FindName(name) is null) namesOwner.RegisterName(name, element);
        }
    }
}
