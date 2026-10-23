using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Steamy.Services;

namespace Steamy.ViewModels;

public sealed class CommunityViewModel : ObservableObject
{
    private readonly ICommunityInviteService _community;
    private CommunityServerInfo _server = CommunityServerInfo.Default;
    private string _status = "";
    public CommunityServerInfo Server { get => _server; private set => SetProperty(ref _server, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public IRelayCommand OpenCommand { get; }
    public IRelayCommand StarRepoCommand { get; }
    public CommunityViewModel(ICommunityInviteService community)
    {
        _community = community;
        OpenCommand = new RelayCommand(() => Open(community.Open));
        StarRepoCommand = new RelayCommand(() => Open(() => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("https://github.com/ZazaJr24/Steamy") { UseShellExecute = true })));
    }
    private void Open(Action action)
    {
        try { action(); Status = ""; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { Status = "Couldn't open the link. Please try again."; }
    }
    public async Task LoadAsync(CancellationToken token)
    {
        try { Server = await _community.GetAsync(token) ?? CommunityServerInfo.Default; Status = ""; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { Status = "Server details are unavailable right now. You can still open the community."; }
    }
}
