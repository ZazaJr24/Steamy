namespace Steamy.Services;

/// <summary>Fallback used by isolated unit tests; production uses SQLite.</summary>
public sealed class NullLocalDatabase : ILocalDatabase
{
    private readonly List<PersistedDownloadJob> _jobs = new();

    public void Initialize() { }
    public Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task AppendLogAsync(Models.LogEntry entry, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SaveDownloadJobAsync(PersistedDownloadJob job, CancellationToken cancellationToken = default)
    {
        _jobs.RemoveAll(existing => existing.Id == job.Id);
        _jobs.Insert(0, job);
        return Task.CompletedTask;
    }

    public Task DeleteDownloadJobAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _jobs.RemoveAll(existing => existing.Id == id);
        return Task.CompletedTask;
    }

    public Task ClearAllDownloadJobsAsync(CancellationToken cancellationToken = default)
    {
        _jobs.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PersistedDownloadJob>> LoadDownloadJobsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PersistedDownloadJob>>(_jobs.ToList());
}
