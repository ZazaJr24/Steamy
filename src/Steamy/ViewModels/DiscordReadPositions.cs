namespace Steamy.ViewModels;

internal sealed record DiscordReadPosition(bool FollowLatest, double Offset, ulong AnchorId = 0, double AnchorTop = 0);

public sealed partial class DiscordFriendsViewModel
{
    private readonly Dictionary<string, DiscordReadPosition> _readPositions = [];
    internal event EventHandler? ConversationChanging;
    internal DiscordReadPosition? ReadPosition(string key) => _readPositions.GetValueOrDefault(key);
    internal void RememberReadPosition(string key, DiscordReadPosition position)
    {
        if (!double.IsFinite(position.Offset) || !double.IsFinite(position.AnchorTop)) return;
        _readPositions.Remove(key); _readPositions[key] = position with { Offset = Math.Max(0, position.Offset) };
        while (_readPositions.Count > 32) _readPositions.Remove(_readPositions.Keys.First());
    }
}
