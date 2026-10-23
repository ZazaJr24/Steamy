using System.Collections;
using System.Globalization;

namespace Steamy.Services;

public sealed record DiscordAudioDevice(string Id, string Name, bool IsDefault);
public sealed record DiscordAudioDevices(IReadOnlyList<DiscordAudioDevice> Inputs, IReadOnlyList<DiscordAudioDevice> Outputs);
public sealed record DiscordVoiceOptions(bool Muted = false, bool Deafened = false, bool PushToTalk = false, string InputId = "", string OutputId = "", float InputVolume = 100, float OutputVolume = 100, int PushToTalkKey = 0x77);
public sealed record DiscordVoiceParticipant(DiscordProfile Profile, bool Speaking, bool Muted, float Volume);
public sealed record DiscordVoiceSnapshot(ulong LobbyId, string Label, bool IsPrivate, string State, bool Muted, bool Deafened, bool PushToTalk, IReadOnlyList<DiscordVoiceParticipant> Participants)
{
    public bool Active => LobbyId != 0;
    public static DiscordVoiceSnapshot Empty { get; } = new(0, "", false, "Disconnected", false, false, false, []);
}
public sealed record DiscordVoiceInvite(string Secret, ulong HostId, ulong GuestId, DateTimeOffset ExpiresAt)
{
    public bool Expired => DateTimeOffset.UtcNow >= ExpiresAt;
}
public interface IDiscordVoiceService
{
    DiscordVoiceSnapshot Voice { get; }
    event EventHandler? VoiceChanged;
    Task JoinPartyVoiceAsync(CancellationToken token = default);
    Task StartPrivateCallAsync(ulong friendId, CancellationToken token = default);
    Task AcceptVoiceInviteAsync(DiscordVoiceInvite invite, CancellationToken token = default);
    Task LeaveVoiceAsync(CancellationToken token = default);
    Task<DiscordAudioDevices> GetAudioDevicesAsync(CancellationToken token = default);
    Task SetVoiceOptionsAsync(DiscordVoiceOptions options, CancellationToken token = default);
    Task SetParticipantVolumeAsync(ulong userId, float volume, CancellationToken token = default);
}
internal static class DiscordVoiceProtocol
{
    internal static Dictionary<string, string> Metadata(ulong application, DiscordVoiceInvite invite) => new()
    {
        ["steamy.kind"] = "private_voice", ["steamy.version"] = "1", ["steamy.application"] = application.ToString(CultureInfo.InvariantCulture),
        ["steamy.host"] = invite.HostId.ToString(CultureInfo.InvariantCulture), ["steamy.guest"] = invite.GuestId.ToString(CultureInfo.InvariantCulture),
        ["steamy.expires"] = invite.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
    };
    internal static DiscordVoiceInvite? Read(IDictionary? metadata, ulong application, ulong authorId, string? secret = null)
    {
        if (metadata is null || metadata["steamy.kind"] as string != "private_voice" || metadata["steamy.version"] as string != "1" || metadata["steamy.application"] as string != application.ToString(CultureInfo.InvariantCulture)) return null;
        secret ??= metadata["steamy.voice_secret"] as string;
        if (secret is not { Length: 64 } || !secret.All(char.IsAsciiHexDigit)
            || !ulong.TryParse(metadata["steamy.host"] as string, out var host) || host == 0 || host != authorId
            || !ulong.TryParse(metadata["steamy.guest"] as string, out var guest) || guest == 0 || guest == host
            || !long.TryParse(metadata["steamy.expires"] as string, out var expires) || expires < 0 || expires > 253402300799) return null;
        var deadline = DateTimeOffset.FromUnixTimeSeconds(expires);
        return deadline > DateTimeOffset.UtcNow.AddMinutes(15) ? null : new(secret, host, guest, deadline);
    }
}
public sealed partial class DiscordSocialService : IDiscordVoiceService
{
    public DiscordVoiceSnapshot Voice => (_client as IDiscordVoiceService)?.Voice ?? DiscordVoiceSnapshot.Empty;
    public event EventHandler? VoiceChanged;
    private void ClientVoiceChanged(object? sender, EventArgs args) { if (ReferenceEquals(sender, _client)) VoiceChanged?.Invoke(this, EventArgs.Empty); }
    private Task<T> VoiceOperationAsync<T>(Func<IDiscordVoiceService, CancellationToken, Task<T>> action, CancellationToken token) =>
        ClientOperationAsync((client, ct) => client is IDiscordVoiceService voice ? action(voice, ct) : throw new DiscordSocialException("Voice isn't available in this Discord SDK build."), true, token);
    private Task VoiceOperationAsync(Func<IDiscordVoiceService, CancellationToken, Task> action, CancellationToken token) => VoiceOperationAsync(async (voice, ct) => { await action(voice, ct); return true; }, token);
    public Task JoinPartyVoiceAsync(CancellationToken token = default) => VoiceOperationAsync((voice, ct) => voice.JoinPartyVoiceAsync(ct), token);
    public Task StartPrivateCallAsync(ulong friendId, CancellationToken token = default)
    { ValidateConversation(friendId, false); return VoiceOperationAsync((voice, ct) => voice.StartPrivateCallAsync(friendId, ct), token); }
    public Task AcceptVoiceInviteAsync(DiscordVoiceInvite invite, CancellationToken token = default)
    {
        if (invite.Expired || invite.GuestId != Snapshot.Profile?.Id || !Snapshot.Relationships.Any(friend => friend.Id == invite.HostId && friend.IsFriend)) throw new DiscordSocialException("This call invitation has expired or isn't addressed to you.");
        return VoiceOperationAsync((voice, ct) => voice.AcceptVoiceInviteAsync(invite, ct), token);
    }
    public Task LeaveVoiceAsync(CancellationToken token = default) => VoiceOperationAsync((voice, ct) => voice.LeaveVoiceAsync(ct), token);
    public Task<DiscordAudioDevices> GetAudioDevicesAsync(CancellationToken token = default) => VoiceOperationAsync((voice, ct) => voice.GetAudioDevicesAsync(ct), token);
    public Task SetVoiceOptionsAsync(DiscordVoiceOptions options, CancellationToken token = default) => VoiceOperationAsync((voice, ct) => voice.SetVoiceOptionsAsync(options, ct), token);
    public Task SetParticipantVolumeAsync(ulong userId, float volume, CancellationToken token = default) => VoiceOperationAsync((voice, ct) => voice.SetParticipantVolumeAsync(userId, volume, ct), token);
}
