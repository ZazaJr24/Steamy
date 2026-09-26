using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Steamy.Models;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class GreenLumaAppIdEntry : ObservableObject
{
    public string Index { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;
}

public sealed class GreenLumaViewModel : ViewModelBase
{
    private string _steamPath = string.Empty;
    private string _statusMessage = "Ready";
    private string _actionStatus = "Add App IDs and press Generate to create the AppList folder.";
    private string _previewText = string.Empty;
    private bool _isInfoVisible;

    public GreenLumaViewModel(IAppDataStore store, INavigationService navigation, ILoggingService logging)
        : base(store, navigation, logging)
    {
        DetectSteamCommand = new RelayCommand(DetectSteam);
        ToggleInfoCommand = new RelayCommand(() => IsInfoVisible = !IsInfoVisible);
        ClearAllCommand = new RelayCommand(ClearAll);
        RemoveAppIdCommand = new RelayCommand<string>(RemoveAppId);
        PreviewCommand = new RelayCommand(Preview);
        GenerateCommand = new RelayCommand(Generate);

        DetectSteam();
    }

    public ObservableCollection<GreenLumaAppIdEntry> AppIds { get; } = new();

    public string SteamPath
    {
        get => _steamPath;
        set
        {
            if (SetProperty(ref _steamPath, value))
            {
                OnPropertyChanged(nameof(SteamPathDisplay));
                OnPropertyChanged(nameof(HasSteamPath));
                UpdateStatus();
            }
        }
    }

    public string SteamPathDisplay => string.IsNullOrWhiteSpace(SteamPath) ? "Not configured" : SteamPath;
    public bool HasSteamPath => !string.IsNullOrWhiteSpace(SteamPath) && Directory.Exists(SteamPath);

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ActionStatus
    {
        get => _actionStatus;
        private set => SetProperty(ref _actionStatus, value);
    }

    public string PreviewText
    {
        get => _previewText;
        private set
        {
            if (SetProperty(ref _previewText, value))
                OnPropertyChanged(nameof(HasPreview));
        }
    }

    public bool HasPreview => !string.IsNullOrWhiteSpace(PreviewText);

    public bool IsInfoVisible
    {
        get => _isInfoVisible;
        private set => SetProperty(ref _isInfoVisible, value);
    }

    public string AppIdCountLabel => AppIds.Count == 0 ? "No App IDs added" : $"{AppIds.Count} App ID(s)";

    public ICommand DetectSteamCommand { get; }
    public ICommand ToggleInfoCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand RemoveAppIdCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand GenerateCommand { get; }

    public void AddAppIds(string input)
    {
        var ids = Regex.Split(input, @"[\s,;]+")
            .Select(s => s.Trim())
            .Where(s => Regex.IsMatch(s, @"^\d{1,10}$"))
            .Distinct()
            .ToList();

        var existing = new HashSet<string>(AppIds.Select(e => e.AppId));
        var added = 0;
        foreach (var id in ids)
        {
            if (existing.Contains(id)) continue;
            existing.Add(id);
            added++;
        }

        if (added > 0)
        {
            RebuildList(existing);
            ActionStatus = $"Added {added} App ID(s). Total: {AppIds.Count}.";
        }
        else if (ids.Count == 0)
        {
            ActionStatus = "No valid App IDs found in the input. Enter numeric Steam App IDs.";
        }
        else
        {
            ActionStatus = "All entered App IDs are already in the list.";
        }
    }

    private void RemoveAppId(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        var entry = AppIds.FirstOrDefault(e => e.AppId == appId);
        if (entry is null) return;

        var remaining = AppIds.Where(e => e.AppId != appId).Select(e => e.AppId).ToHashSet();
        RebuildList(remaining);
        ActionStatus = $"Removed App ID {appId}. {AppIds.Count} remaining.";
    }

    private void ClearAll()
    {
        AppIds.Clear();
        PreviewText = string.Empty;
        OnPropertyChanged(nameof(AppIdCountLabel));
        ActionStatus = "All App IDs cleared.";
    }

    private void RebuildList(IEnumerable<string> ids)
    {
        var sorted = ids.OrderBy(id => int.TryParse(id, out var n) ? n : int.MaxValue).ToList();
        AppIds.Clear();
        for (var i = 0; i < sorted.Count; i++)
        {
            AppIds.Add(new GreenLumaAppIdEntry
            {
                Index = $"{i}.txt",
                AppId = sorted[i]
            });
        }
        OnPropertyChanged(nameof(AppIdCountLabel));
    }

    private void Preview()
    {
        if (AppIds.Count == 0)
        {
            ActionStatus = "Add at least one App ID first.";
            PreviewText = string.Empty;
            return;
        }

        var sb = new StringBuilder();
        var appListPath = HasSteamPath ? Path.Combine(SteamPath, "AppList") : @"<Steam>\AppList";
        sb.AppendLine($"Target folder: {appListPath}");
        sb.AppendLine($"Files to create: {AppIds.Count}");
        sb.AppendLine("─────────────────────────────────");

        foreach (var entry in AppIds)
            sb.AppendLine($"  {entry.Index}  →  {entry.AppId}");

        PreviewText = sb.ToString();
        ActionStatus = "Preview generated. Press Generate to write the files.";
    }

    private void Generate()
    {
        if (AppIds.Count == 0)
        {
            ActionStatus = "Add at least one App ID before generating.";
            return;
        }

        if (!HasSteamPath)
        {
            ActionStatus = "Set a valid Steam directory first.";
            return;
        }

        try
        {
            var appListDir = Path.Combine(SteamPath, "AppList");

            if (Directory.Exists(appListDir))
            {
                foreach (var file in Directory.EnumerateFiles(appListDir, "*.txt"))
                {
                    try { File.Delete(file); } catch { }
                }
            }
            else
            {
                Directory.CreateDirectory(appListDir);
            }

            for (var i = 0; i < AppIds.Count; i++)
            {
                var filePath = Path.Combine(appListDir, $"{i}.txt");
                File.WriteAllText(filePath, AppIds[i].AppId, Encoding.UTF8);
            }

            ActionStatus = $"AppList generated: {AppIds.Count} file(s) written to {appListDir}";
            Logging.Add(LogLevel.Info, "GreenLuma", $"Generated {AppIds.Count} AppList entries in {appListDir}.");

            Preview();
        }
        catch (Exception ex)
        {
            ActionStatus = $"Failed to generate AppList: {ex.Message}";
            Logging.Add(LogLevel.Error, "GreenLuma", $"AppList generation failed: {ex.Message}");
        }
    }

    private void DetectSteam()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            var path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                var normalized = path.Replace('/', '\\');
                if (Directory.Exists(normalized))
                {
                    SteamPath = normalized;
                    StatusMessage = "Steam detected";
                    return;
                }
            }
        }
        catch { }

        var defaultPaths = new[]
        {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
            @"D:\SteamLibrary"
        };

        foreach (var p in defaultPaths)
        {
            if (!Directory.Exists(p)) continue;
            SteamPath = p;
            StatusMessage = "Steam found at common path";
            return;
        }

        StatusMessage = "Steam not found — set the path manually";
    }

    private void UpdateStatus()
    {
        if (HasSteamPath)
        {
            var appListDir = Path.Combine(SteamPath, "AppList");
            if (Directory.Exists(appListDir))
            {
                var count = Directory.EnumerateFiles(appListDir, "*.txt").Count();
                StatusMessage = count > 0 ? $"AppList exists with {count} entries" : "AppList folder exists (empty)";
            }
            else
            {
                StatusMessage = "Steam directory set — AppList will be created on Generate";
            }
        }
        else
        {
            StatusMessage = string.IsNullOrWhiteSpace(SteamPath) ? "Set your Steam path" : "Steam directory not found";
        }
    }
}
