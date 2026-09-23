using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using SteamContentManager.Models;
using SteamContentManager.ViewModels;

namespace SteamContentManager.Pages;

public partial class FamilySharePage : Page
{
    public FamilySharePage()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<FamilyShareViewModel>();
        ViewModel.Suggestions.CollectionChanged += Suggestions_Changed;
    }

    private FamilyShareViewModel ViewModel => (FamilyShareViewModel)DataContext;

    private void Suggestions_Changed(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SuggestionsPanel.Visibility = ViewModel.Suggestions.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Suggestion_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border && border.DataContext is SteamSearchEntry entry)
        {
            ViewModel.SelectSuggestion(entry);
            SuggestionsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        ViewModel.Suggestions.Clear();
        SuggestionsPanel.Visibility = Visibility.Collapsed;
        ViewModel.AddFromInput(text);
        SearchBox.Text = string.Empty;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var text = SearchBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        ViewModel.Suggestions.Clear();
        SuggestionsPanel.Visibility = Visibility.Collapsed;
        ViewModel.AddFromInput(text);
        SearchBox.Text = string.Empty;
    }

    private async void GenerateAndLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PrepareManifestsCommand.CanExecute(null))
            await ViewModel.PrepareManifestsCommand.ExecuteAsync(null);
        ViewModel.GenerateCommand.Execute(null);
        ViewModel.LaunchCommand.Execute(null);
    }
}
