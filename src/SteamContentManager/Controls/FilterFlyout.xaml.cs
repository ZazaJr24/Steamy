using System.Windows;
using System.Windows.Controls;

namespace SteamContentManager.Controls;

public partial class FilterFlyout : UserControl
{
    public FilterFlyout()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncPopupContent();
        UpdateActiveIndicator();
    }

    public object? FilterContent
    {
        get => GetValue(FilterContentProperty);
        set => SetValue(FilterContentProperty, value);
    }

    public static readonly DependencyProperty FilterContentProperty =
        DependencyProperty.Register(
            nameof(FilterContent),
            typeof(object),
            typeof(FilterFlyout),
            new PropertyMetadata(null, OnFlyoutContentChanged));

    public string FilterSummary
    {
        get => (string)GetValue(FilterSummaryProperty);
        set => SetValue(FilterSummaryProperty, value);
    }

    public static readonly DependencyProperty FilterSummaryProperty =
        DependencyProperty.Register(
            nameof(FilterSummary),
            typeof(string),
            typeof(FilterFlyout),
            new PropertyMetadata("No filters applied", OnFlyoutSummaryChanged));

    public bool HasActiveFilters
    {
        get => (bool)GetValue(HasActiveFiltersProperty);
        set => SetValue(HasActiveFiltersProperty, value);
    }

    public static readonly DependencyProperty HasActiveFiltersProperty =
        DependencyProperty.Register(
            nameof(HasActiveFilters),
            typeof(bool),
            typeof(FilterFlyout),
            new PropertyMetadata(false, OnHasActiveFiltersChanged));

    private static void OnFlyoutContentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((FilterFlyout)dependencyObject).SyncPopupContent();
    }

    private static void OnFlyoutSummaryChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        var control = (FilterFlyout)dependencyObject;
        if (control.SummaryText is not null)
            control.SummaryText.Text = control.FilterSummary;
    }

    private static void OnHasActiveFiltersChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((FilterFlyout)dependencyObject).UpdateActiveIndicator();
    }

    private void SyncPopupContent()
    {
        if (ContentHost is not null)
            ContentHost.Content = FilterContent;
        if (SummaryText is not null)
            SummaryText.Text = FilterSummary;
    }

    private void UpdateActiveIndicator()
    {
        if (ActiveDot is not null)
            ActiveDot.Visibility = HasActiveFilters ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        FilterToggle.IsChecked = false;
    }
}
