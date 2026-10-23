namespace Steamy.Services;

/// <summary>Do not send an invitation to a room until its host has connected to voice.</summary>
internal static class DiscordPrivateCallFlow
{
    internal static async Task StartAsync(Func<CancellationToken, Task<ulong>> create,
        Func<ulong, CancellationToken, Task> connect, Func<CancellationToken, Task> invite,
        Func<ulong, Task> cleanup, CancellationToken token)
    {
        var id = await create(token);
        if (id == 0) throw new DiscordSocialException("Discord call setup returned an unavailable voice room. Start a new call.");
        try
        {
            token.ThrowIfCancellationRequested(); await connect(id, token);
            token.ThrowIfCancellationRequested(); await invite(token);
        }
        catch { try { await cleanup(id); } catch { } throw; }
    }
}

internal sealed class DiscordVoiceConnection
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _started;
    internal void Update(string state, string error, int detail)
    {
        if (state != "Disconnected") _started = true;
        if (state == "Connected") _ready.TrySetResult();
        else if (state == "Disconnected" && (error != "None" || _started))
            _ready.TrySetException(new DiscordSocialException(ErrorMessage(error, detail)));
    }
    internal static string ErrorMessage(string error, int detail) => error switch
    {
        "Forbidden" => "Discord refused the voice connection. Check this application's voice access and your Discord permissions.",
        "JoinTimeout" => "The Discord voice connection timed out. Start a new call when your connection is ready.",
        "SignalingConnectionFailed" or "SignalingUnexpectedClose" => "Discord couldn't open the voice room. Start a new call; if it repeats, reconnect Discord.",
        _ => "Discord couldn't connect voice. Check your connection and audio devices, then start a new call."
    } + (detail > 0 ? " (voice detail " + detail + ")" : "");
    internal async Task WaitAsync(CancellationToken token)
    {
        try { await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), token); }
        catch (TimeoutException) { throw new DiscordSocialException("The Discord voice connection timed out. Start a new call when your connection is ready."); }
    }
}
