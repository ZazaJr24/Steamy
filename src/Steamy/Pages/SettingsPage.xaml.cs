using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.Services;
using Steamy.ViewModels;
using Wpf.Ui.Appearance;
using System.ComponentModel;
using Steamy.Controls;
using Steamy.Models;

namespace Steamy.Pages;

public partial class SettingsPage : Page
{
    private string _selectedCategory = "general";
    private readonly Dictionary<FrameworkElement, string> _sectionSearchText = new();
    private readonly System.Windows.Threading.DispatcherTimer _searchTimer = new(System.Windows.Threading.DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(180) };
    private AppSettings? _motionSettings;

    public SettingsPage()
    {
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); FilterSections(); };
        Resources.Add("StringVis", new SettingsStringToVisibilityConverter());
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<SettingsViewModel>();

        // After a save or a delete the typed secrets are gone from memory; the boxes have to match,
        // otherwise they would keep showing characters that no longer mean anything.
        Loaded += (_, _) =>
        {
            ViewModel.CredentialInputsCleared -= OnCredentialInputsCleared;
            ViewModel.CredentialInputsCleared += OnCredentialInputsCleared;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            ObserveMotionSettings();
            FilterSections();
        };
        Unloaded += (_, _) =>
        {
            _searchTimer.Stop();
            ViewModel.CredentialInputsCleared -= OnCredentialInputsCleared;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            if (_motionSettings is not null) _motionSettings.PropertyChanged -= MotionSettings_PropertyChanged;
            _motionSettings = null;
            OnCredentialInputsCleared(this, EventArgs.Empty);
        };
        FilterSections();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.Settings)) ObserveMotionSettings();
    }

    private void ObserveMotionSettings()
    {
        if (_motionSettings is not null) _motionSettings.PropertyChanged -= MotionSettings_PropertyChanged;
        _motionSettings = ViewModel.Settings;
        _motionSettings.PropertyChanged += MotionSettings_PropertyChanged;
        MotionPreferences.Configure(_motionSettings.ReduceEffects);
    }

    private void MotionSettings_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppSettings.DashboardSearch))
            App.Services.GetRequiredService<DashboardViewModel>().RefreshSearchPreference();
        if (args.PropertyName == nameof(AppSettings.ReduceEffects))
            MotionPreferences.Configure(ViewModel.Settings.ReduceEffects);
    }

    private void Settings_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            SettingsSearch.Focus();
            SettingsSearch.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape && !string.IsNullOrEmpty(SettingsSearch.Text))
        {
            SettingsSearch.Clear();
            _searchTimer.Stop();
            FilterSections();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && SettingsSearch.IsKeyboardFocusWithin)
        {
            _searchTimer.Stop();
            FilterSections();
            e.Handled = true;
        }
    }

    private void Category_Checked(object sender, RoutedEventArgs e)
    {
        _searchTimer.Stop();
        if (sender is RadioButton { Tag: string category }) _selectedCategory = category;
        FilterSections();
    }

    private void SettingsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) => SettingsSearch.Clear();

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("Restore the default application settings?", "Reset settings",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            && ViewModel.ResetCommand.CanExecute(null))
            ViewModel.ResetCommand.Execute(null);
    }

    private void FilterSections()
    {
        // Checked/TextChanged also fire while InitializeComponent is still building the page.
        if (SectionsPanel is null || SettingsSearch is null || SectionSummary is null) return;
        var words = SettingsSearch.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        var changed = false;
        foreach (FrameworkElement section in SectionsPanel.Children)
        {
            if (!_sectionSearchText.TryGetValue(section, out var searchable))
            {
                searchable = string.Join(" ", StaticLabels(section));
                _sectionSearchText[section] = searchable;
            }
            var visible = words.Length > 0
                ? words.All(word => searchable.Contains(word, StringComparison.OrdinalIgnoreCase))
                : _selectedCategory == "all" || string.Equals(section.Tag as string, _selectedCategory, StringComparison.Ordinal);
            var next = visible ? Visibility.Visible : Visibility.Collapsed;
            changed |= section.Visibility != next;
            if (section.Visibility != next) section.Visibility = next;
            if (visible) count++;
        }
        SectionSummary.Text = words.Length > 0 ? $"{count} matching sections across all settings" : $"{count} sections · changes save automatically";
        NoSettingsResults.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (changed)
        {
            SettingsScrollViewer.ScrollToTop();
            if (IsLoaded) Steamy.Controls.EntranceMotion.Reveal(SectionsPanel);
        }
    }


    private static IEnumerable<string> StaticLabels(DependencyObject element)
    {
        // Index UI labels only, never paths, entered settings or password contents.
        if (element is TextBlock text && !BindingOperations.IsDataBound(text, TextBlock.TextProperty)) yield return text.Text;
        foreach (var child in LogicalTreeHelper.GetChildren(element).OfType<DependencyObject>())
            foreach (var label in StaticLabels(child)) yield return label;
    }

    private void OnCredentialInputsCleared(object? sender, EventArgs e)
    {
        SteamApiKeyBox.Clear();
        RyuuAuthKeyBox.Clear();
        HubcapApiKeyBox.Clear();
        DepotBoxApiKeyBox.Clear();
        ManifestHubApiKeyBox.Clear();
        MirrorTokenBox.Clear();
        ShareTokenBox.Clear();
    }

    private void NumberBox_LostFocus(object sender, RoutedEventArgs e) => ViewModel.NormalizeNumberFields();

    private SettingsViewModel ViewModel => (SettingsViewModel)DataContext;

    /// <summary>
    /// Secrets are not part of the settings object, so the settings autosave never notices them.
    /// Every password box therefore hands its value to the view model and asks it to store it.
    /// An emptied box is ignored: that is either the page clearing itself after a save or the
    /// "empty means keep the current key" case, and neither should write anything.
    /// </summary>
    private void CredentialTyped(PasswordBox box, string what, Action<string> assign)
    {
        if (string.IsNullOrWhiteSpace(box.Password)) return;

        assign(box.Password);
        ViewModel.OnCredentialInputChanged(what);
    }

    private void AppearanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // The first event comes from filling in the saved value when the page is built, not from the user.
        if (e.RemovedItems.Count == 0 || e.AddedItems.Count == 0 || e.AddedItems[0] is not string selection)
            return;

        ViewModel.Settings.Appearance = selection;
        UiThemeService.Apply(selection);
    }

    private void BackdropComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0 || e.AddedItems.Count == 0 || e.AddedItems[0] is not string selection)
            return;

        ViewModel.Settings.BackdropStyle = selection;
        UiThemeService.ApplyBackdrop(selection);
    }

    private void DnsModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.RemovedItems.Count == 0 || e.AddedItems.Count == 0 || e.AddedItems[0] is not string selection)
            return;

        ViewModel.ApplyDnsMode(selection);
    }

    private void SteamApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            CredentialTyped(passwordBox, "Steam API key", value => ViewModel.SteamApiKeyInput = value);
    }

    private void RyuuAuthKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            CredentialTyped(passwordBox, "Ryuu auth key", value => ViewModel.RyuuAuthKeyInput = value);
    }

    private void HubcapApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            CredentialTyped(passwordBox, "Hubcap API key", value => ViewModel.HubcapApiKeyInput = value);
    }

    private void DepotBoxApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            CredentialTyped(passwordBox, "DepotBox API key", value => ViewModel.DepotBoxApiKeyInput = value);
    }

    private void ManifestHubApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            CredentialTyped(passwordBox, "ManifestHub API key", value => ViewModel.ManifestHubApiKeyInput = value);
    }

    private void MirrorTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            CredentialTyped(passwordBox, "Access token", value => ViewModel.MirrorTokenInput = value);
    }

    private void ShareTokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox)
            CredentialTyped(passwordBox, "Dump sharing token", value => ViewModel.ShareTokenInput = value);
    }

    private void SteamLibraryBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select your Steam folder",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.SteamLibraryPath = dialog.FolderName;
    }

    private void DepotDownloaderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select DepotDownloader executable",
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.DepotDownloaderPath = dialog.FileName;
    }

    private void WorkingDirectoryBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select DepotDownloader working directory",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.WorkingDirectory = dialog.FolderName;
    }

    private void DownloadFolderBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select default download folder",
            Multiselect = false,
            ValidateNames = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ViewModel.Settings.DownloadFolder = dialog.FolderName;
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        // An exception in an async event handler would close the window; a failed export is a
        // message, not a crash.
        try
        {
            await ExportAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"The settings could not be exported.{Environment.NewLine}{exception.Message}",
                "Steamy",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task ExportAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export safe settings",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "steam-content-manager-settings.json",
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        await ViewModel.ExportAsync(dialog.FileName);
    }
}

internal sealed class SettingsStringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
