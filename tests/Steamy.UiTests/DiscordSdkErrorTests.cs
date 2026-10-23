using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordSdkErrorTests
{
    [Theory]
    [InlineData("CreateOrJoinLobbyWithMetadata", "lobby setup")]
    [InlineData("SendUserMessageWithMetadata", "direct message")]
    [InlineData("GetUserMessagesWithLimit", "conversation history")]
    public void UnknownChannelNamesTheFailedStageWithoutBlamingCredentials(string operation, string stage)
    {
        var error = DiscordSdkErrors.Create(operation, "HTTPError", 404, 10003, "{\"message\":\"private-secret\"}");
        Assert.Contains(stage, error.Message); Assert.Contains("10003", error.Message);
        Assert.DoesNotContain("username", error.Message); Assert.DoesNotContain("Public Client", error.Message);
        Assert.DoesNotContain("private-secret", error.Message);
    }
    [Fact]
    public void OnlyDesktopApprovalRpc5000UsesTheBrowserFallback()
    {
        Assert.IsType<DiscordDesktopApprovalException>(DiscordSdkErrors.Create("Authorize", "RPCError", 0, 5000, null));
        Assert.IsNotType<DiscordDesktopApprovalException>(DiscordSdkErrors.Create("GetToken", "RPCError", 0, 5000, null));
        Assert.IsNotType<DiscordDesktopApprovalException>(DiscordSdkErrors.Create("Authorize", "Aborted", 0, 5000, null));
        Assert.IsNotType<DiscordDesktopApprovalException>(DiscordSdkErrors.Create("Authorize", "RPCError", 0, 4001, null));
    }

    [Fact]
    public void PublicClientFailureNamesTokenExchangeAndKeepsConfigurationSeparateFromCredentials()
    {
        var error = DiscordSdkErrors.Create("GetToken", "HTTPError", 401, 0, "{\"error\":\"invalid_client\"}");
        Assert.IsType<DiscordSocialException>(error);
        Assert.Contains("token exchange", error.Message);
        Assert.Contains("Public Client", error.Message);
        Assert.DoesNotContain("username", error.Message);
    }

    [Fact]
    public void RefreshOfAnInvalidGrantClearsRevokedCredentials()
    {
        var error = DiscordSdkErrors.Create("RefreshToken", "HTTPError", 400, 0, "{\"error\":\"invalid_grant\"}");
        Assert.IsType<DiscordAuthorizationException>(error);
        Assert.Contains("session refresh", error.Message);
    }

    [Theory]
    [InlineData("{\"error\":\"invalid_client\",\"error_description\":\"secret-token\",\"access_token\":\"secret-token\"}")]
    [InlineData("{\"error\":\"secret-token\"}")]
    [InlineData("secret-token <html>Bad Gateway</html>")]
    [InlineData("{\"error\":{\"token\":\"secret-token\"}}")]
    public void DiagnosticsNeverExposeRawResponsesOrUnknownErrorText(string body)
    {
        var error = DiscordSdkErrors.Create("secret-token", "secret-token", 400, 0, body);
        Assert.DoesNotContain("secret-token", error.Message);
    }

    [Fact]
    public void FriendPrivacyFailureDoesNotSuggestChangingPublicClient()
    {
        var error = DiscordSdkErrors.Create("SendDiscordFriendRequest", "HTTPError", 403, 80001, null);
        Assert.Contains("privacy", error.Message);
        Assert.DoesNotContain("Public Client", error.Message);
    }
}
