using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Steamy.Models;

namespace Steamy.Services;

public enum DiscordRelationshipKind { Friend, PendingIncoming, PendingOutgoing, Blocked }
public enum DiscordFriendAction { Accept, Reject, Cancel, Remove, Block, Unblock }
public sealed record DiscordProfile(ulong Id, string Username, string DisplayName, string? AvatarUrl);
public sealed record DiscordFriend(DiscordProfile Profile, DiscordRelationshipKind Kind, string Status)
{
    public ulong Id => Profile.Id;
    public string Username => Profile.Username;
    public string DisplayName => Profile.DisplayName;
    public string? AvatarUrl => Profile.AvatarUrl;
    public string Initial => DisplayName.Length > 0 ? StringInfo.GetNextTextElement(DisplayName) : "?";
    public bool IsOnline => Kind == DiscordRelationshipKind.Friend && Status != "Offline";
    public bool IsIncoming => Kind == DiscordRelationshipKind.PendingIncoming;
    public bool IsOutgoing => Kind == DiscordRelationshipKind.PendingOutgoing;
    public bool IsFriend => Kind == DiscordRelationshipKind.Friend;
    public bool IsBlocked => Kind == DiscordRelationshipKind.Blocked;
    public string Detail => Kind switch
    {
        DiscordRelationshipKind.PendingIncoming => "Wants to be your friend",
        DiscordRelationshipKind.PendingOutgoing => "Friend request sent",
        DiscordRelationshipKind.Blocked => "Blocked",
        _ => Status
    };
}
public sealed record DiscordSnapshot(bool IsConnected, DiscordProfile? Profile, IReadOnlyList<DiscordFriend> Relationships)
{
    public static DiscordSnapshot Disconnected { get; } = new(false, null, []);
}
public sealed record DiscordSession(ulong ApplicationId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt,
    string Scopes = "openid sdk.social_layer_presence");

public interface IDiscordSocialService
{
    bool IsAvailable { get; }
    bool HasSavedSession { get; }
    string AvailabilityMessage { get; }
    DiscordSnapshot Snapshot { get; }
    event EventHandler? Changed;
    Task RestoreAsync(CancellationToken token = default);
    Task ConnectAsync(CancellationToken token = default);
    Task DisconnectAsync(CancellationToken token = default);
    Task SendFriendRequestAsync(string usernameOrId, CancellationToken token = default);
    Task UpdateFriendAsync(DiscordFriend friend, DiscordFriendAction action, CancellationToken token = default);
}

internal interface IDiscordSocialClient : IDisposable
{
    DiscordSnapshot Snapshot { get; }
    event EventHandler? Changed;
    event EventHandler? TokenExpired;
    Task<DiscordSession> AuthorizeAsync(ulong applicationId, CancellationToken token);
    Task<DiscordSession> RefreshAsync(DiscordSession session, CancellationToken token);
    Task ConnectAsync(ulong applicationId, string accessToken, CancellationToken token);
    Task RevokeAsync(DiscordSession session, CancellationToken token);
    Task SendFriendRequestAsync(string usernameOrId, CancellationToken token);
    Task UpdateFriendAsync(ulong userId, DiscordFriendAction action, CancellationToken token);
}

/// <summary>Only the public application identifier is part of settings/build metadata.</summary>
public static class DiscordConfiguration
{
    public static string DefaultApplicationId => typeof(DiscordConfiguration).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "DiscordApplicationId")?.Value ?? "";
    public static bool TryApplicationId(string? value, out ulong id) => ulong.TryParse(value,
        NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    public static string SdkDirectory => Path.Combine(AppContext.BaseDirectory, "Tools", "Discord");
    public static bool SdkInstalled => File.Exists(Path.Combine(SdkDirectory, "Discord.Sdk.dll"))
        && File.Exists(Path.Combine(SdkDirectory, "discord_partner_sdk.dll"));
}

/// <summary>Explicit opt-in, one encrypted session, and no dependency on a Steam account.</summary>
public sealed partial class DiscordSocialService : IDiscordSocialService, IDisposable
{
    internal const string SessionCredentialKey = "discord-social-session";
    private readonly ISettingsService _settings;
    private readonly ISecureCredentialService _credentials;
    private readonly Func<IDiscordSocialClient> _createClient;
    private readonly Func<bool> _sdkInstalled;
    private readonly SemaphoreSlim _gate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private IDiscordSocialClient? _client;
    private CancellationTokenSource? _clientOperations;
    private DiscordSession? _session;
    private bool _restoreAttempted;
    private bool _disposed;
    public DiscordSnapshot Snapshot => _client?.Snapshot ?? DiscordSnapshot.Disconnected;
    public bool HasSavedSession => _session is not null;
    public event EventHandler? Changed;
    public bool IsAvailable => DiscordConfiguration.TryApplicationId(_settings.Load().DiscordApplicationId, out _)
        && _sdkInstalled();
    public string AvailabilityMessage => !DiscordConfiguration.TryApplicationId(_settings.Load().DiscordApplicationId, out _)
        ? "Discord login isn't configured for this build yet. You can connect later."
        : !_sdkInstalled() ? "Discord support isn't installed in this build yet. You can connect later."
        : "Connect to see your friends and send requests from Steamy.";

    public DiscordSocialService(ISettingsService settings, ISecureCredentialService credentials)
        : this(settings, credentials, () => new HostedDiscordSdkClient(DiscordConfiguration.SdkDirectory), () => DiscordConfiguration.SdkInstalled) { }

    internal DiscordSocialService(ISettingsService settings, ISecureCredentialService credentials,
        Func<IDiscordSocialClient> createClient, Func<bool> sdkInstalled)
    { _settings = settings; _credentials = credentials; _createClient = createClient; _sdkInstalled = sdkInstalled; }

    public async Task RestoreAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if ((_restoreAttempted && _session is null) || !IsAvailable || Snapshot.IsConnected) return;
            var stored = await _credentials.ReadAsync(SessionCredentialKey, token);
            _restoreAttempted = true;
            if (string.IsNullOrWhiteSpace(stored)) return;
            DiscordSession? session;
            try { session = JsonSerializer.Deserialize<DiscordSession>(stored); }
            catch (JsonException) { session = null; }
            if (session is null || !DiscordConfiguration.TryApplicationId(_settings.Load().DiscordApplicationId, out var applicationId)
                || applicationId != session.ApplicationId || string.IsNullOrEmpty(session.AccessToken)
                || string.IsNullOrEmpty(session.RefreshToken))
            {
                await _credentials.DeleteAsync(SessionCredentialKey, token);
                return;
            }
            _session = session;
            using var timeout = OperationTimeout(token, 45);
            var client = Client();
            if (session.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                _session = session = await client.RefreshAsync(session, timeout.Token);
                await SaveSessionAsync(session, timeout.Token);
            }
            await client.ConnectAsync(session.ApplicationId, session.AccessToken, timeout.Token);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (DiscordAuthorizationException)
        {
            _session = null;
            await _credentials.DeleteAsync(SessionCredentialKey, CancellationToken.None);
            ReleaseClient();
            throw;
        }
        catch { _restoreAttempted = false; ReleaseClient(); throw; }
        finally { _gate.Release(); }
    }

    public async Task ConnectAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (!IsAvailable) throw new InvalidOperationException(AvailabilityMessage);
            DiscordConfiguration.TryApplicationId(_settings.Load().DiscordApplicationId, out var applicationId);
            if (Snapshot.IsConnected && _session?.ApplicationId == applicationId && HasCommunicationPermission) return;
            using var timeout = OperationTimeout(token, 180);
            ReleaseClient();
            var client = Client();
            _session = await client.AuthorizeAsync(applicationId, timeout.Token);
            await SaveSessionAsync(_session, timeout.Token);
            _restoreAttempted = true;
            await client.ConnectAsync(_session.ApplicationId, _session.AccessToken, timeout.Token);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch { ReleaseClient(); throw; }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            // Local sign-out always finishes, even when Discord is unreachable.
            await _credentials.DeleteAsync(SessionCredentialKey, CancellationToken.None);
            var session = _session;
            _session = null;
            _restoreAttempted = true;
            try
            {
                if (_client is not null && session is not null)
                {
                    using var timeout = OperationTimeout(token, 8);
                    await _client.RevokeAsync(session, timeout.Token);
                }
            }
            catch (Exception exception) when (exception is DiscordSocialException or OperationCanceledException) { }
            finally { ReleaseClient(); }
        }
        finally { _gate.Release(); }
    }

    public Task SendFriendRequestAsync(string usernameOrId, CancellationToken token = default)
    {
        var name = usernameOrId.Trim().TrimStart('@');
        if (name.Length is < 2 or > 64 || name.Any(char.IsControl))
            throw new ArgumentException("Enter a Discord username or user ID.");
        return RunFriendOperationAsync((client, operationToken) => client.SendFriendRequestAsync(name, operationToken), token);
    }

    public Task UpdateFriendAsync(DiscordFriend friend, DiscordFriendAction action, CancellationToken token = default)
    {
        var valid = action switch
        {
            DiscordFriendAction.Accept or DiscordFriendAction.Reject => friend.IsIncoming,
            DiscordFriendAction.Cancel => friend.IsOutgoing,
            DiscordFriendAction.Remove => friend.IsFriend,
            DiscordFriendAction.Unblock => friend.IsBlocked,
            DiscordFriendAction.Block => !friend.IsBlocked,
            _ => false
        };
        if (!valid || friend.Id == 0) throw new ArgumentException("This action is no longer available. Refresh your friends and try again.");
        return RunFriendOperationAsync((client, operationToken) => client.UpdateFriendAsync(friend.Id, action, operationToken), token);
    }

    private Task RunFriendOperationAsync(Func<IDiscordSocialClient, CancellationToken, Task> action, CancellationToken token) =>
        ClientOperationAsync(async (client, ct) => { await action(client, ct); return true; }, false, token);

    private async Task<T> ClientOperationAsync<T>(Func<IDiscordSocialClient, CancellationToken, Task<T>> action, bool communication, CancellationToken token)
    {
        IDiscordSocialClient client; CancellationTokenSource pending;
        await _gate.WaitAsync(token);
        try
        {
            ThrowIfDisposed();
            if (_client is null || !Snapshot.IsConnected) throw new InvalidOperationException("Connect Discord before continuing.");
            if (communication && !HasCommunicationPermission) throw new InvalidOperationException("Reconnect Discord to enable chat and parties.");
            if (!DiscordConfiguration.TryApplicationId(_settings.Load().DiscordApplicationId, out var applicationId) || _session?.ApplicationId != applicationId)
                throw new InvalidOperationException("The Discord application changed. Disconnect and sign in again.");
            client = _client;
            pending = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, _clientOperations!.Token);
            pending.CancelAfter(TimeSpan.FromSeconds(30));
        }
        finally { _gate.Release(); }
        // The lifecycle lock protects acquiring a client, never the network wait.
        // Disconnect cancels every lease before disposing the native client on its host.
        using (pending)
        {
            var result = await action(client, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(client, _client)) throw new OperationCanceledException(pending.Token);
            Changed?.Invoke(this, EventArgs.Empty); return result;
        }
    }

    private IDiscordSocialClient Client()
    {
        if (_client is not null) return _client;
        _client = _createClient();
        _clientOperations = new CancellationTokenSource();
        _client.Changed += ClientChanged;
        if (_client is IDiscordVoiceService voice) voice.VoiceChanged += ClientVoiceChanged;
        _client.TokenExpired += ClientTokenExpired;
        if (_client is IDiscordCommunicationClient chat) chat.MessageChanged += ClientMessageChanged;
        return _client;
    }
    private void ClientChanged(object? sender, EventArgs args) => Changed?.Invoke(this, EventArgs.Empty);
    private async void ClientTokenExpired(object? sender, EventArgs args)
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed || _client is null || !ReferenceEquals(sender, _client) || _session is null) return;
            using var timeout = OperationTimeout(CancellationToken.None, 30);
            _session = await _client.RefreshAsync(_session, timeout.Token);
            await SaveSessionAsync(_session, timeout.Token);
            await _client.ConnectAsync(_session.ApplicationId, _session.AccessToken, timeout.Token);
        }
        catch (DiscordAuthorizationException)
        {
            _session = null;
            try { await _credentials.DeleteAsync(SessionCredentialKey, CancellationToken.None); } catch { }
            ReleaseClient();
        }
        catch { ReleaseClient(); }
        finally { _gate.Release(); }
    }
    private Task SaveSessionAsync(DiscordSession session, CancellationToken token) =>
        _credentials.SaveAsync(SessionCredentialKey, JsonSerializer.Serialize(session), token);
    private CancellationTokenSource OperationTimeout(CancellationToken token, int seconds)
    { var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(seconds)); return timeout; }
    private void ReleaseClient()
    {
        var pending = _clientOperations; _clientOperations = null;
        pending?.Cancel(); pending?.Dispose();
        if (_client is not null)
        {
            var client = _client;
            _client = null;
            client.Changed -= ClientChanged; client.TokenExpired -= ClientTokenExpired;
            if (client is IDiscordVoiceService voice) voice.VoiceChanged -= ClientVoiceChanged;
            if (client is IDiscordCommunicationClient chat) chat.MessageChanged -= ClientMessageChanged;
            client.Dispose();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); ReleaseClient(); }
}

public class DiscordSocialException(string message) : Exception(message);
public sealed class DiscordAuthorizationException(string message) : DiscordSocialException(message);
