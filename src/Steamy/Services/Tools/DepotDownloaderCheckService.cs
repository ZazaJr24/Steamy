namespace Steamy.Services;

public interface IDepotDownloaderCheckService
{
    Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default);
}

/// <summary>Uses the same bounded, runtime-aware check as the download adapter.</summary>
public sealed class DepotDownloaderCheckService : IDepotDownloaderCheckService
{
    public async Task<DepotDownloaderToolStatus> CheckAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        using var adapter = new DepotDownloaderService();
        return await adapter.CheckAsync(executablePath, cancellationToken).ConfigureAwait(false);
    }
}
