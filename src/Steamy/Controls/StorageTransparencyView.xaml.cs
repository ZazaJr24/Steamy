using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.Services;
using Steamy.ViewModels;
using Steamy.Storage;

namespace Steamy.Controls;

public partial class StorageTransparencyView : UserControl
{
    public ObservableCollection<StorageGroup> Groups { get; } = new();
    private CancellationTokenSource? _inspection;
    public StorageTransparencyView()
    {
        InitializeComponent(); DataContext = this;
        Loaded += (_, _) => Refresh();
        IsVisibleChanged += (_, e) => { if (IsLoaded && (bool)e.NewValue) Refresh(); };
        Unloaded += (_, _) => _inspection?.Cancel();
    }
    public void Refresh()
    {
        var settings = App.Services.GetRequiredService<SettingsViewModel>().Settings;
        var store = App.Services.GetRequiredService<IAppDataStore>();
        Groups.Clear(); foreach (var group in StorageTransparency.Describe(settings, store.Downloads, store.Games)) Groups.Add(group);
        var location = AppDataPaths.Locations.Read();
        DataFolderInput.Text = location.PendingRoot ?? AppDataPaths.Root;
        DataFolderStatus.Text = location.PendingRoot is { } pending
            ? $"Pending: {pending} · takes effect on next start. Current: {AppDataPaths.Root}"
            : $"Current: {AppDataPaths.Root}";
        CancelFolderChange.Visibility = location.PendingRoot is null ? Visibility.Collapsed : Visibility.Visible;
        SnapshotStatus.Text = $"Updated {DateTime.Now:t} · {Groups.Sum(group => group.Locations.Count)} documented locations · expand a group to inspect";
        TrackingStatus.Text = settings.TrackPlayTime
            ? "Playtime tracking is enabled in Settings. PlayTimeTracker.exe can keep collecting local game duration after Steamy closes. Turn off Playtime tracking in General to stop collection."
            : "Playtime tracking is disabled in Settings. Previously saved local time remains until you explicitly remove it.";
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();
    private void BrowseDataFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose an empty folder for Steamy data", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) DataFolderInput.Text = dialog.FolderName;
    }
    private void ChangeDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { AppDataPaths.Locations.Schedule(DataFolderInput.Text.Trim()); Refresh(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { DataFolderStatus.Text = error.Message; }
    }
    private void CancelDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { AppDataPaths.Locations.Cancel(); Refresh(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { DataFolderStatus.Text = error.Message; }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string path }) return;
        var folder = Directory.Exists(path) ? path : File.Exists(path) ? Path.GetDirectoryName(path) : null;
        if (folder is null) { SnapshotStatus.Text = "This location does not exist yet. No folder was created."; return; }
        try { using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { SnapshotStatus.Text = "The folder could not be opened."; }
    }
    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string { Length: > 0 } path })
        {
            try { Clipboard.SetText(path); SnapshotStatus.Text = "Path copied."; }
            catch (System.Runtime.InteropServices.COMException) { SnapshotStatus.Text = "The clipboard is busy. Try again."; }
        }
    }
    private async void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "Save storage transparency report", Filter = "Text report (*.txt)|*.txt", FileName = "Steamy-storage-report.txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        Refresh();
        try { await File.WriteAllTextAsync(dialog.FileName, StorageTransparency.Report(Groups.ToArray())); SnapshotStatus.Text = "Report saved. It contains paths and explanations, no file contents or keys."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { SnapshotStatus.Text = "The report could not be saved to this location."; }
    }
    private async void InspectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Identify a file without running it", Filter = "Executables and libraries (*.exe;*.dll)|*.exe;*.dll|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        _inspection?.Cancel(); _inspection?.Dispose(); _inspection = new();
        InspectFileButton.IsEnabled = false; InspectionResult.Text = "Calculating SHA-256 locally…";
        try
        {
            await using var file = new FileStream(dialog.FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(file, _inspection.Token);
            InspectionResult.Text = $"{dialog.FileName}\nSHA-256: {Convert.ToHexString(hash).ToLowerInvariant()}\nFile was not executed or uploaded. This is an identifier, not a safety verdict.";
        }
        catch (OperationCanceledException) { InspectionResult.Text = "File identification cancelled."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { InspectionResult.Text = "This file could not be read. No file was run or uploaded."; }
        finally { InspectFileButton.IsEnabled = true; }
    }
    private void OfficialLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string link } || !Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host is not ("learn.microsoft.com" or "www.microsoft.com")) return;
        try { using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { SnapshotStatus.Text = "The official website could not be opened."; }
    }
}
