using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace SteamContentManager.ViewModels;

public sealed class ToolStatusViewModel : ObservableObject
{
    private bool _isInfoVisible;

    public ToolStatusViewModel(string title, string subtitle, string statusTitle, string statusMessage, string? infoText = null)
    {
        Title = title;
        Subtitle = subtitle;
        StatusTitle = statusTitle;
        StatusMessage = statusMessage;
        InfoText = infoText ?? "This page is informational only. No executable workflow is configured.";
        ToggleInfoCommand = new RelayCommand(() => IsInfoVisible = !IsInfoVisible);
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string StatusTitle { get; }
    public string StatusMessage { get; }
    public string InfoText { get; }
    public bool IsInfoVisible
    {
        get => _isInfoVisible;
        private set => SetProperty(ref _isInfoVisible, value);
    }

    public ICommand ToggleInfoCommand { get; }
}
