using System.Windows.Controls;
namespace Steamy.Controls;
public partial class SocialPartyPanel : UserControl
{
    public SocialPartyPanel() => InitializeComponent();
    internal void ShowInvitations() => InvitationStack.BringIntoView();
}
