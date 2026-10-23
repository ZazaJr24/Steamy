using System.Collections;
using System.Security.Cryptography;
using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordPartyCodeTests
{
    [Fact]
    public void PartyCodesRoundTripAndRejectMistypedOrTruncatedSecrets()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var code = DiscordPartyCode.Encode(secret);
        Assert.True(DiscordPartyCode.TryDecode(code, out var decoded)); Assert.Equal(secret, decoded);
        Assert.False(DiscordPartyCode.TryDecode(code[..^1], out _));
        Assert.False(DiscordPartyCode.TryDecode(code.Replace("STMY1", "STMY2"), out _));
        var changed = code[..6] + (code[6] == 'A' ? 'B' : 'A') + code[7..];
        Assert.False(DiscordPartyCode.TryDecode(changed, out _));
    }
    [Fact]
    public void NewlyCreatedEmptyLobbiesPrivateCallsAndForeignApplicationsAreNotGameParties()
    {
        IDictionary metadata = new Dictionary<string, string>();
        Assert.Throws<DiscordSocialException>(() => DiscordLobbyProtocol.ReadParty(metadata, 42));
        metadata = DiscordLobbyProtocol.PartyMetadata(42, 1, new(123, "A game"));
        Assert.Equal(123, DiscordLobbyProtocol.ReadParty(metadata, 42).AppId);
        Assert.Throws<DiscordSocialException>(() => DiscordLobbyProtocol.ReadParty(metadata, 43));
        metadata["steamy.kind"] = "private_voice";
        Assert.Throws<DiscordSocialException>(() => DiscordLobbyProtocol.ReadParty(metadata, 42, true));
    }
}
