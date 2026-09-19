using System.Collections.ObjectModel;
using SteamContentManager.Models;

namespace SteamContentManager.Services;

public interface IGameFixProvider
{
    IReadOnlyList<string> SupportedSources { get; }
    Task<IReadOnlyList<GameFixItem>> SearchAsync(string gameName, int appId, string source, CancellationToken cancellationToken = default);
}

public sealed class DemoGameFixProvider : IGameFixProvider
{
    public IReadOnlyList<string> SupportedSources { get; } = new[]
    {
        "CS.RIN.RU",
        "SteamMidra",
        "GamesCopyWorld",
        "Project Lightning",
        "Other hosts"
    };

    public Task<IReadOnlyList<GameFixItem>> SearchAsync(string gameName, int appId, string source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = CreateEntries(gameName, appId, source);
        return Task.FromResult<IReadOnlyList<GameFixItem>>(entries);
    }

    private static IReadOnlyList<GameFixItem> CreateEntries(string gameName, int appId, string source)
    {
        return source switch
        {
            "CS.RIN.RU" => new List<GameFixItem>
            {
                new GameFixItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GameName = gameName,
                    AppId = appId,
                    Source = "CS.RIN.RU",
                    Type = "Fix",
                    Name = "Online fix package",
                    Description = "Latest multiplayer fix for the selected game.",
                    Version = "1.0",
                    FileName = "fix.7z",
                    Size = "45 MB",
                    DownloadUrl = "https://cs.rin.ru/download/fix.7z"
                },
                new GameFixItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GameName = gameName,
                    AppId = appId,
                    Source = "CS.RIN.RU",
                    Type = "Crack",
                    Name = "DRM-free launcher patch",
                    Description = "Replaces the default launcher entry point.",
                    Version = "1.0",
                    FileName = "launcher_patch.exe",
                    Size = "12 MB",
                    DownloadUrl = "https://cs.rin.ru/download/launcher_patch.exe"
                }
            },
            "SteamMidra" => new List<GameFixItem>
            {
                new GameFixItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GameName = gameName,
                    AppId = appId,
                    Source = "SteamMidra",
                    Type = "Fix",
                    Name = "Steam compatibility fix",
                    Description = "Steam-side compatibility fix for the selected game.",
                    Version = "1.0",
                    FileName = "steammidra_fix.zip",
                    Size = "30 MB",
                    DownloadUrl = "https://steammidra.example/fix.zip"
                }
            },
            "GamesCopyWorld" => new List<GameFixItem>
            {
                new GameFixItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GameName = gameName,
                    AppId = appId,
                    Source = "GamesCopyWorld",
                    Type = "Game files",
                    Name = "Complete game package",
                    Description = "Game files matching the selected title.",
                    Version = "1.0",
                    FileName = "gamecopyworld_package.zip",
                    Size = "85 GB",
                    DownloadUrl = "https://gamescopyworld.example/package.zip"
                }
            },
            "Project Lightning" => new List<GameFixItem>
            {
                new GameFixItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GameName = gameName,
                    AppId = appId,
                    Source = "Project Lightning",
                    Type = "Bypass",
                    Name = "Bypass file",
                    Description = "Project Lightning bypass package for the selected game.",
                    Version = "1.0",
                    FileName = "bypass.7z",
                    Size = "18 MB",
                    DownloadUrl = "https://projectlightning.example/bypass.7z"
                },
                new GameFixItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GameName = gameName,
                    AppId = appId,
                    Source = "Project Lightning",
                    Type = "Online fix",
                    Name = "Online fix package",
                    Description = "Online fix provided through Project Lightning.",
                    Version = "1.0",
                    FileName = "onlinefix.zip",
                    Size = "22 MB",
                    DownloadUrl = "https://projectlightning.example/onlinefix.zip"
                }
            },
            _ => new List<GameFixItem>
            {
                new GameFixItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    GameName = gameName,
                    AppId = appId,
                    Source = source,
                    Type = "Fix",
                    Name = "Generic fix entry",
                    Description = "Best available fix entry for the selected game and source.",
                    Version = "1.0",
                    FileName = "fix.zip",
                    Size = "50 MB",
                    DownloadUrl = "https://example/fix.zip"
                }
            }
        };
    }
}
