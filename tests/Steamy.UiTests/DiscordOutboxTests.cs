using Steamy.Services;
using Steamy.ViewModels;

namespace Steamy.UiTests;

public sealed partial class DiscordWorkspaceTests
{
    [Fact]
    public async Task AnUnconfirmedSendCannotBeBlindlyRetriedAndAHistoryConfirmationDeduplicatesIt()
    {
        var service = new SocialFixture { SendError = new TimeoutException() };
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "One message";
        await model.SendMessageCommand.ExecuteAsync(null);
        var failed = Assert.Single(model.Messages); Assert.False(failed.CanResend);
        await model.RetryMessageCommand.ExecuteAsync(failed);
        Assert.Equal(1, service.SendCalls); Assert.Equal(DiscordDeliveryState.Failed, Assert.Single(model.Messages).DeliveryState);
        service.History = (_, _) => Task.FromResult<IReadOnlyList<DiscordChatMessage>>([failed with { Id = 901, DeliveryState = DiscordDeliveryState.Sent }]);
        await model.RetryMessageCommand.ExecuteAsync(failed);
        Assert.Equal(1, service.SendCalls); Assert.Equal(901UL, Assert.Single(model.Messages).Id); Assert.Equal(DiscordDeliveryState.Sent, model.Messages[0].DeliveryState);
    }
    [Fact]
    public async Task ExplicitlyRejectedMessagesCanRetryWithoutLeavingDuplicateBubbles()
    {
        var service = new SocialFixture { SendError = new DiscordSendRejectedException("Privacy prevented delivery.") };
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "Retry after privacy change";
        await model.SendMessageCommand.ExecuteAsync(null);
        var failed = Assert.Single(model.Messages); Assert.True(failed.CanResend);
        service.SendError = null; await model.RetryMessageCommand.ExecuteAsync(failed);
        Assert.Equal(2, service.SendCalls); Assert.Equal(DiscordDeliveryState.Sent, Assert.Single(model.Messages).DeliveryState);
        Assert.Equal(failed.ClientNonce, model.Messages[0].ClientNonce);
    }
    [Fact]
    public async Task ASlowSendDoesNotDisableAnotherConversationAndItsConfirmationSurvivesSwitchingBack()
    {
        var service = new SocialFixture { SendCompletion = new() };
        using var model = new DiscordFriendsViewModel(service, new Settings());
        model.SelectedFriend = model.Friends[0]; model.MessageDraft = "Conversation two";
        var send = model.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(DiscordDeliveryState.Sending, Assert.Single(model.Messages).DeliveryState);
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 3); model.MessageDraft = "Conversation three draft";
        Assert.True(model.SendMessageCommand.CanExecute(null));
        service.SendCompletion.SetResult(Message(100, 2, "Conversation two") with { AuthorId = 1, IsOwn = true });
        await send;
        Assert.Empty(model.Messages); Assert.Equal("Conversation three draft", model.MessageDraft);
        model.SelectedFriend = model.Friends.First(friend => friend.Id == 2);
        await Task.Delay(10);
        Assert.Equal(100UL, Assert.Single(model.Messages).Id); Assert.Equal(DiscordDeliveryState.Sent, model.Messages[0].DeliveryState);
    }
}
