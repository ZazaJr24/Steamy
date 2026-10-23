using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;

namespace Steamy.Services;

public sealed record CommunityServerInfo(string Name, string? IconUrl, int? Members, int? Online)
{
    public static CommunityServerInfo Default { get; } = new("Steamy", null, null, null);
    public string Activity => Members is { } members && Online is { } online ? $"{members:N0} members · {online:N0} online" : "Find your next teammate in the Steamy community.";
}
public interface ICommunityInviteService
{
    Task<CommunityServerInfo?> GetAsync(CancellationToken token = default);
    void Open();
}
/// <summary>Public invite metadata only. No bot, server member enumeration, token or account access.</summary>
public sealed class CommunityInviteService : ICommunityInviteService
{
    public const string InviteUrl = "https://discord.gg/eYyaFABabC";
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _fetch = new(1);
    private CommunityServerInfo? _cached;
    private DateTimeOffset _expires;
    public async Task<CommunityServerInfo?> GetAsync(CancellationToken token = default)
    {
        if (_cached is not null && _expires > DateTimeOffset.UtcNow) return _cached;
        await _fetch.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_cached is not null && _expires > DateTimeOffset.UtcNow) return _cached;
            using var response = await Client.GetAsync("https://discord.com/api/v10/invites/eYyaFABabC?with_counts=true", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 65536) return _cached;
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var memory = new System.IO.MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            { if (memory.Length + read > 65536) return _cached; await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); }
            _cached = Parse(memory.ToArray()); _expires = DateTimeOffset.UtcNow.AddMinutes(5); return _cached;
        }
        finally { _fetch.Release(); }
    }
    internal static CommunityServerInfo Parse(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes); var root = json.RootElement; var guild = root.GetProperty("guild");
        if (guild.GetProperty("id").GetString() != "1556047592100470977") throw new InvalidOperationException("The community invite has changed.");
        var name = guild.GetProperty("name").GetString() ?? "Steamy"; if (name.Length > 100) name = name[..100];
        var icon = guild.TryGetProperty("icon", out var iconValue) && iconValue.ValueKind == JsonValueKind.String ? iconValue.GetString() : null;
        var iconUrl = icon is { Length: > 0 and <= 66 } && icon.All(character => char.IsAsciiHexDigit(character) || character is '_' or 'a')
            ? $"https://cdn.discordapp.com/icons/1556047592100470977/{icon}.png?size=256" : null;
        int? Count(string key) => root.TryGetProperty(key, out var value) && value.TryGetInt32(out var count) && count is >= 0 and <= 100000000 ? count : null;
        return new(name, iconUrl, Count("approximate_member_count"), Count("approximate_presence_count"));
    }
    public void Open() => Process.Start(new ProcessStartInfo(InviteUrl) { UseShellExecute = true });
}
