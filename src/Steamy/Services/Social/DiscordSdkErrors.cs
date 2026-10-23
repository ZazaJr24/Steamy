using System.Text.Json;

namespace Steamy.Services;

internal static class DiscordSdkErrors
{
    private static readonly HashSet<string> KnownOAuthErrors = ["invalid_client", "invalid_grant", "invalid_scope", "invalid_request", "unauthorized_client", "access_denied", "unsupported_grant_type"];
    private static readonly HashSet<string> KnownSdkErrors = ["None", "NetworkError", "HTTPError", "ClientNotReady", "Disabled", "ClientDestroyed", "ValidationError", "Aborted", "AuthorizationFailed", "RPCError"];

    public static DiscordSocialException Create(string operation, string type, int status, int code, string? responseBody)
    {
        var oauth = ReadOAuthError(responseBody);
        if (operation == "Authorize" && type == "RPCError" && code == 5000)
            return new DiscordDesktopApprovalException();
        var signIn = operation is "Authorize" or "GetToken" or "RefreshToken" or "UpdateToken";
        var stage = operation switch
        {
            "Authorize" => "Discord approval",
            "GetToken" => "Discord token exchange",
            "RefreshToken" => "Discord session refresh",
            "UpdateToken" => "Discord session setup",
            "CreateOrJoinLobby" or "CreateOrJoinLobbyWithMetadata" => "Discord lobby setup",
            "SendUserMessage" or "SendUserMessageWithMetadata" => "Discord direct message",
            "SendLobbyMessage" or "SendLobbyMessageWithMetadata" => "Discord party message",
            "GetLobbyMessagesWithLimit" => "Discord party history",
            "GetUserMessagesWithLimit" => "Discord conversation history",
            "SetInputDevice" => "Discord microphone selection",
            "SetOutputDevice" => "Discord speaker selection",
            _ => "Discord request"
        };
        var advice = oauth switch
        {
            "invalid_client" or "unauthorized_client" => "Check that Public Client is enabled and saved for the Steamy application in Discord's Developer Portal.",
            "invalid_grant" => "The authorization code or session is no longer valid. Sign in again and check the application's redirect URL.",
            "invalid_scope" => "Check that the Social SDK and requested communication scopes are enabled for the Discord application.",
            "access_denied" => "Discord permission was declined. You can connect whenever you're ready.",
            _ when type == "AuthorizationFailed" || status == 401 => "Discord authorization is no longer valid. Sign in again.",
            _ when type == "Aborted" => "Sign-in was cancelled. You can connect whenever you're ready.",
            _ when status == 429 => "Too many requests. Wait a moment and try again.",
            _ when type == "NetworkError" => "Check your connection and try again.",
            _ when type == "ClientNotReady" => "Discord isn't connected yet. Reconnect and try again.",
            _ when type == "Disabled" => "This feature isn't enabled for the application's current Discord access level.",
            _ when signIn && status == 400 => "Check Public Client and the redirect URL in Discord's Developer Portal, then start a fresh sign-in.",
            _ when signIn && status == 403 => "Discord refused this application's access. Check its Social SDK setup and permissions in the Developer Portal.",
            _ when signIn => "Discord couldn't complete sign-in. Check the application's OAuth2 setup and try again.",
            _ when code == 80001 => "Discord privacy or blocking settings prevent this request.",
            _ when code == 10003 && operation is "CreateOrJoinLobby" or "CreateOrJoinLobbyWithMetadata" => "Discord couldn't open the voice or party room. Start a new call or party; reconnect Discord if it repeats.",
            _ when code == 10003 => "This Discord channel is no longer available. Reopen the conversation or start a new call; reconnect Discord if it repeats.",
            _ => "Check your connection, username and Discord privacy settings, then try again."
        };
        var details = new List<string>();
        if (status > 0) details.Add("HTTP " + status);
        if (KnownSdkErrors.Contains(type) && type != "None") details.Add(type);
        if (code != 0) details.Add("code " + code);
        if (oauth is not null) details.Add(oauth);
        var message = stage + " failed. " + advice + (details.Count == 0 ? "" : " (" + string.Join(", ", details) + ")");
        if (operation.StartsWith("SendUserMessage", StringComparison.Ordinal) || operation.StartsWith("SendLobbyMessage", StringComparison.Ordinal))
            if (type is "ValidationError" or "ClientNotReady" or "Disabled" or "ClientDestroyed" || status is 400 or 401 or 403 or 404 or 413 or 422 or 429)
                return new DiscordSendRejectedException(message);
        // A client configuration error must not delete a valid saved session.
        return oauth is not ("invalid_client" or "unauthorized_client") && (oauth == "invalid_grant" || type == "AuthorizationFailed" || status == 401)
            ? new DiscordAuthorizationException(message) : new DiscordSocialException(message);
    }

    private static string? ReadOAuthError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 32768) return null;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String) return null;
            var value = error.GetString();
            return value is not null && KnownOAuthErrors.Contains(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }
}

internal sealed class DiscordDesktopApprovalException() : DiscordSocialException("The Discord desktop client couldn't approve sign-in (RPCError 5000). Please use the browser sign-in.");
