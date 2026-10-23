using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Steamy.ViewModels;

namespace Steamy.Controls;

public partial class DiscordConnectionCard : UserControl
{
    public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(nameof(Model), typeof(DiscordFriendsViewModel),
        typeof(DiscordConnectionCard), new PropertyMetadata(null));
    public DiscordFriendsViewModel? Model { get => (DiscordFriendsViewModel?)GetValue(ModelProperty); set => SetValue(ModelProperty, value); }
    public DiscordConnectionCard()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (!Services.DiscordIntegration.Enabled) return;
            Model ??= App.Services?.GetService<DiscordFriendsViewModel>();
            if (Model is not null) await Model.InitializeAsync();
        };
    }
    public void CancelPendingOperation() => Model?.CancelCommand.Execute(null);
}
