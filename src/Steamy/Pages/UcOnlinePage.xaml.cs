using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Steamy.ViewModels;
using UCO2.Patcher.Core;

namespace Steamy.Pages;

public partial class UcOnlinePage : Page
{
    public UcOnlinePage() : this(App.Services.GetRequiredService<UcOnlineViewModel>()) { }
    public UcOnlinePage(UcOnlineViewModel model)
    {
        InitializeComponent(); DataContext = model;
        Loaded += (_, _) => {model.PropertyChanged += ModelChanged; EnsureSettings();};
        Unloaded += (_, _) => model.PropertyChanged -= ModelChanged;
    }
    private UcOnlineViewModel Model => (UcOnlineViewModel)DataContext;
    private PatchOptions? _builtOptions;
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(UcOnlineViewModel.Options)) EnsureSettings(); }
    private void Settings_Expanded(object sender,RoutedEventArgs args)=>EnsureSettings();
    private void EnsureSettings()
    {
        if(!SettingsExpander.IsExpanded || ReferenceEquals(_builtOptions,Model.Options)) return;
        BuildSettings(); _builtOptions=Model.Options;
    }
    private void BuildSettings()
    {
        OptionsFields.Children.Clear();
        // Closed by default; all upstream options remain available without crowding the workflow.
        foreach (var property in typeof(PatchOptions).GetProperties().Where(p => p.PropertyType != typeof(uint) && p.PropertyType != typeof(Dictionary<string, string>)))
        {
            var label = Regex.Replace(property.Name, "([a-z])([A-Z])", "$1 $2");
            var binding = new Binding("Options." + property.Name) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };
            if (property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
            {
                var check = new CheckBox { Content = label, IsThreeState = property.PropertyType == typeof(bool?), Margin = new Thickness(0, 5, 0, 5) };
                check.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
                check.SetBinding(CheckBox.IsCheckedProperty, binding); OptionsFields.Children.Add(check); continue;
            }
            var caption = new TextBlock { Text = label, Margin = new Thickness(0, 12, 0, 6), FontSize = 12 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush"); OptionsFields.Children.Add(caption);
            if (property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase))
            {
                var password = new PasswordBox { Password = property.GetValue(Model.Options) as string ?? "" }; password.PasswordChanged += (_, _) => property.SetValue(Model.Options, password.Password); OptionsFields.Children.Add(password); continue;
            }
            var field = new TextBox { MinHeight = 34, Padding = new Thickness(10, 7, 10, 7) };
            if (property.Name is "AdvancedIni" or "ManualDlc") { field.AcceptsReturn = true; field.TextWrapping = TextWrapping.Wrap; field.Height = 110; field.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            field.SetBinding(TextBox.TextProperty, binding); OptionsFields.Children.Add(field);
        }
    }
    private void Folder_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "Choose your game folder" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) Model.Folder = dialog.FolderName; }
    private void Executable_Click(object sender, RoutedEventArgs e)
    { var dialog = new OpenFileDialog { Title = "Choose the running game executable", Filter = "Executable (*.exe)|*.exe" }; if (dialog.ShowDialog(Window.GetWindow(this)) == true) Model.Executable = dialog.FileName; }
    private void Folder_DragOver(object sender, DragEventArgs e)
    { e.Effects = Model.CanEdit && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && Directory.Exists(paths[0]) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private void Folder_Drop(object sender, DragEventArgs e)
    { if (Model.CanEdit && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } paths && Directory.Exists(paths[0])) Model.Folder = paths[0]; e.Handled = true; }
}
