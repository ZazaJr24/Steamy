using Steamy.Services;

namespace Steamy.UiTests;

public sealed class DiscordPrivateCallFlowTests
{
    [Fact]
    public async Task InvitationWaitsForTheActualVoiceConnection()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var call = DiscordPrivateCallFlow.StartAsync(_ => { events.Add("create"); return Task.FromResult(42UL); },
            async (id, token) => { Assert.Equal(42UL, id); events.Add("connect"); await ready.Task.WaitAsync(token); },
            _ => { events.Add("invite"); return Task.CompletedTask; }, _ => { events.Add("cleanup"); return Task.CompletedTask; }, default);
        Assert.Equal(["create", "connect"], events); Assert.False(call.IsCompleted);
        ready.SetResult(); await call;
        Assert.Equal(["create", "connect", "invite"], events);
    }

    [Theory]
    [InlineData("create", 0, 0)]
    [InlineData("connect", 0, 1)]
    [InlineData("invite", 1, 1)]
    public async Task ChannelFailureLeavesTheOwnedRoomWithoutRetryingAnInvitation(string stage, int expectedInvites, int expectedCleanup)
    {
        var sent = 0; var cleaned = 0;
        var error = DiscordSdkErrors.Create(stage == "create" ? "CreateOrJoinLobbyWithMetadata" : "SendUserMessageWithMetadata", "HTTPError", 404, 10003, null);
        var thrown = await Assert.ThrowsAnyAsync<DiscordSocialException>(() => DiscordPrivateCallFlow.StartAsync(
            _ => stage == "create" ? Task.FromException<ulong>(error) : Task.FromResult(42UL),
            (_, _) => stage == "connect" ? Task.FromException(error) : Task.CompletedTask,
            _ => { sent++; return Task.FromException(error); },
            id => { Assert.Equal(42UL, id); cleaned++; return Task.CompletedTask; }, default));
        Assert.Same(error, thrown); Assert.Equal(expectedInvites, sent); Assert.Equal(expectedCleanup, cleaned);
    }

    [Fact]
    public async Task CancellingConnectionCleansTheLobbyAndNeverSendsAnInvitation()
    {
        using var cancel = new CancellationTokenSource(); var cleaned = false; var sent = false;
        var call = DiscordPrivateCallFlow.StartAsync(_ => Task.FromResult(42UL),
            (_, token) => Task.Delay(Timeout.Infinite, token), _ => { sent = true; return Task.CompletedTask; },
            _ => { cleaned = true; return Task.CompletedTask; }, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(cleaned); Assert.False(sent);
    }

    [Fact]
    public async Task SignalingIsNotConnectedAndVoiceFailureIsReportedBeforeInviting()
    {
        var connection = new DiscordVoiceConnection(); var wait = connection.WaitAsync(default);
        connection.Update("SignalingConnected", "None", 0); Assert.False(wait.IsCompleted);
        connection.Update("Disconnected", "Forbidden", 10003);
        var failure = await Assert.ThrowsAsync<DiscordSocialException>(() => wait);
        Assert.Contains("voice access", failure.Message); Assert.Contains("10003", failure.Message);
        var connected = new DiscordVoiceConnection(); var success = connected.WaitAsync(default);
        connected.Update("Connected", "None", 0); await success;
        Assert.DoesNotContain("private-secret", DiscordVoiceConnection.ErrorMessage("private-secret", 0));
    }
}
