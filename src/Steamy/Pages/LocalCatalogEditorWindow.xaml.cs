using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Steamy.Services;

namespace Steamy.Pages;

public partial class LocalCatalogEditorWindow : Window
{
    private readonly LocalGameCatalog _catalog;
    private readonly string _initialKind;
    private List<LocalGamePackage> _items = [];
    private string? _id;
    private LocalGamePackage? _saved;
    private bool _busy, _ready;
    public LocalCatalogEditorWindow(LocalGameCatalog catalog, string kind = "Fix", LocalGamePackage? selected = null)
    {
        InitializeComponent();
        _catalog = catalog; _initialKind = kind;
        CatalogLocation.Text = catalog.CatalogPath;
        _ready = true;
        LoadForm(selected);
        Loaded += async (_, _) => await ReloadAsync(selected?.Id);
        Closing += (_, e) => { if (_busy || !CanDiscard()) e.Cancel = true; };
    }
    internal async Task ReloadAsync(string? selectId = null)
    {
        try
        {
            _items = await _catalog.ReadAsync();
            Filter();
            if (selectId is not null) PackageList.SelectedItem = _items.FirstOrDefault(item => item.Id == selectId);
            Status.Text = $"{_items.Count} packages · Local only";
        }
        catch (Exception error) { Status.Text = "Catalog not loaded: " + error.Message; }
    }
    private void Filter() => PackageList.ItemsSource = _items.Where(item => item.Label.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).OrderBy(item => item.Name).ToArray();
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (_ready && CanDiscard()) Filter(); }
    private LocalGamePackage FormValue() => new()
    {
        Id = _id ?? "new", Kind = ((ComboBoxItem)Kind.SelectedItem).Content.ToString()!, Name = GameName.Text.Trim(),
        AppId = AppId.Text.Trim(), Version = Version.Text.Trim(), ArchivePath = ArchivePath.Text.Trim(),
        Steps = Steps.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Notes = Notes.Text
    };
    private bool Dirty => _saved is not null && System.Text.Json.JsonSerializer.Serialize(FormValue()) != System.Text.Json.JsonSerializer.Serialize(_saved);
    private bool CanDiscard() => !_ready || !Dirty || MessageBox.Show(this, "Discard the unsaved edits?", "Local Catalog Editor", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    private void LoadForm(LocalGamePackage? package)
    {
        _id = package?.Id;
        Kind.SelectedIndex = 0;
        GameName.Text = package?.Name ?? ""; AppId.Text = package?.AppId ?? "";
        Version.Text = package?.Version ?? ""; ArchivePath.Text = package?.ArchivePath ?? "";
        Steps.Text = string.Join(Environment.NewLine, package?.Steps ?? []); Notes.Text = package?.Notes ?? "";
        DeleteButton.IsEnabled = _id is not null;
        _saved = FormValue();
    }
    private void Selection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PackageList.SelectedItem is not LocalGamePackage package || package.Id == _id) return;
        if (CanDiscard()) LoadForm(package);
        else { _ready = false; PackageList.SelectedItem = _items.FirstOrDefault(item => item.Id == _id); _ready = true; }
    }
    private void New_Click(object sender, RoutedEventArgs e) { if (CanDiscard()) { PackageList.SelectedItem = null; LoadForm(null); } }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select local package", Filter = "Archives|*.zip;*.7z;*.rar", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ArchivePath.Text = dialog.FileName;
    }
    private void SetBusy(bool busy)
    {
        _busy = busy; EditorForm.IsEnabled = !busy; PackageList.IsEnabled = !busy;
        SaveButton.IsEnabled = NewButton.IsEnabled = ImportButton.IsEnabled = Search.IsEnabled = !busy;
        DeleteButton.IsEnabled = !busy && _id is not null;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            var package = FormValue() with { Id = _id ?? Guid.NewGuid().ToString("N") };
            LocalGameCatalog.Validate(package);
            if (!File.Exists(package.ArchivePath)) throw new FileNotFoundException("Choose an existing local archive.");
            await _catalog.UpsertAsync(package);
            LoadForm(package);
            await ReloadAsync(package.Id);
            Status.Text = "Saved locally. Refresh Game fixes to show updated entries.";
        }
        catch (Exception error) { Status.Text = "Not saved: " + error.Message; }
        finally { SetBusy(false); }
    }
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_id is null || MessageBox.Show(this, "Remove this catalog entry? The archive file will be kept.", "Local Catalog Editor", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try { await _catalog.RemoveAsync(_id); LoadForm(null); await ReloadAsync(); }
        catch (Exception error) { Status.Text = error.Message; }
        finally { SetBusy(false); }
    }
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDiscard()) return;
        var dialog = new OpenFolderDialog { Title = "Import local archives" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true); Status.Text = "Reading archive metadata…";
        try
        {
            var count = await _catalog.ImportFolderAsync(dialog.FolderName, ((ComboBoxItem)Kind.SelectedItem).Content.ToString()!);
            LoadForm(null); await ReloadAsync(); Status.Text = $"Imported {count} new packages. Existing entries were kept.";
        }
        catch (Exception error) { Status.Text = "Import failed: " + error.Message; }
        finally { SetBusy(false); }
    }
}
