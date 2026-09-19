using System.Collections.Generic;
using System.Linq;
using SteamContentManager.Services;

namespace SteamContentManager.Controllers;

public static class ZazaController
{
    public static ZazaHubViewModel GetZazaHubViewModel()
    {
        return new ZazaHubViewModel
        {
            Games = ZazaRepositoryService.Games,
            CmdxEntries = ZazaRepositoryService.CmdxEntries
        };
    }
}

public class ZazaHubViewModel
{
    public IReadOnlyList<ZazaRepositoryService.GameEntry> Games { get; set; } = new List<ZazaRepositoryService.GameEntry>();
    public IReadOnlyList<ZazaRepositoryService.GameEntry> CmdxEntries { get; set; } = new List<ZazaRepositoryService.GameEntry>();
}

