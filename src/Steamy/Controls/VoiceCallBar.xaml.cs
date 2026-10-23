using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Controls;
public partial class VoiceCallBar : UserControl
{
    public VoiceCallBar() { InitializeComponent(); DataContext = App.Services.GetRequiredService<DiscordVoiceViewModel>(); Unloaded += (_, _) => VoiceSettingsPopup.IsOpen = false; }
    private void VoiceSettings_Click(object sender, RoutedEventArgs args)
    { VoiceSettingsPopup.IsOpen = !VoiceSettingsPopup.IsOpen; if (VoiceSettingsPopup.IsOpen) ((DiscordVoiceViewModel)DataContext).RefreshDevicesCommand.Execute(null); }
}
