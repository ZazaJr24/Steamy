using System.Collections;
using System.Globalization;
using System.Security.Cryptography;

namespace Steamy.Services;

public interface IDiscordPartyCodes
{
    string? PartyCode { get; }
    Task JoinPartyCodeAsync(string code, CancellationToken token = default);
}
public static class DiscordPartyCode
{
    public static string Encode(string secret)
    {
        var bytes = Convert.FromHexString(secret);
        if (bytes.Length != 32) throw new ArgumentException("Invalid party secret.");
        return "STMY1." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + Convert.ToHexString(SHA256.HashData(bytes).AsSpan(0, 4));
    }
    public static bool TryDecode(string code, out string secret)
    {
        secret = ""; var parts = code.Trim().Split('.');
        if (parts.Length != 3 || parts[0] != "STMY1" || parts[1].Length != 43 || parts[2].Length != 8) return false;
        try
        {
            var bytes = Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/') + "=");
            if (bytes.Length != 32 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes).AsSpan(0, 4), Convert.FromHexString(parts[2]))) return false;
            secret = Convert.ToHexString(bytes); return true;
        }
        catch (FormatException) { return false; }
    }
}
internal static class DiscordLobbyProtocol
{
    internal static Dictionary<string, string> PartyMetadata(ulong application, ulong owner, DiscordPartyGame game) => new()
    {
        ["steamy.kind"] = "party", ["steamy.version"] = "1", ["steamy.application"] = application.ToString(CultureInfo.InvariantCulture),
        ["steamy.creator"] = owner.ToString(CultureInfo.InvariantCulture), ["steamy.app_id"] = game.AppId.ToString(CultureInfo.InvariantCulture), ["steamy.game_name"] = game.Name
    };
    internal static DiscordPartyGame ReadParty(IDictionary? metadata, ulong application, bool allowLegacy = false)
    {
        if (metadata is null) throw new DiscordSocialException("This party has ended or its game information is missing. Ask for a new invitation.");
        var kind = metadata["steamy.kind"] as string;
        var typed = kind == "party" && metadata["steamy.version"] as string == "1" && metadata["steamy.application"] as string == application.ToString(CultureInfo.InvariantCulture);
        if (!typed && !(allowLegacy && kind is null)) throw new DiscordSocialException("This code isn't an active Steamy game party. Check the code and your friend's Steamy version.");
        var name = metadata["steamy.game_name"] as string;
        if (!int.TryParse(metadata["steamy.app_id"] as string, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || string.IsNullOrWhiteSpace(name) || name.Length > 200)
            throw new DiscordSocialException("This party has ended or its game information is missing. Ask for a new invitation.");
        return new(id, name);
    }
}
public sealed partial class DiscordSocialService : IDiscordPartyCodes
{
    public string? PartyCode => (_client as IDiscordPartyCodes)?.PartyCode;
    public Task JoinPartyCodeAsync(string code, CancellationToken token = default)
    {
        if (!DiscordPartyCode.TryDecode(code, out _)) throw new ArgumentException("Paste the complete STMY1 party code.");
        return CommunicationOperationAsync(async (client, ct) =>
        {
            if (Party is not null) throw new InvalidOperationException("Leave your current party before joining with a code.");
            if (client is not IDiscordPartyCodes codes) throw new DiscordSocialException("Update Steamy to join with a party code.");
            await codes.JoinPartyCodeAsync(code, ct); return true;
        }, token);
    }
}
